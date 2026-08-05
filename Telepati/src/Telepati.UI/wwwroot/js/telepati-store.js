// Browser-side persistence for the web app.
//
// Why IndexedDB and not localStorage — the obvious first choice:
//
//   localStorage is synchronous, so every read blocks the main thread and janks the UI. It is
//   capped around 5 MB, which one active conversation with media previews can exhaust. It
//   stores strings only, so a thread must be serialised whole and re-parsed whole on every
//   change. And it has no index, so "the newest 50 messages of this chat" means loading and
//   sorting everything.
//
//   IndexedDB is asynchronous, is measured in hundreds of MB, stores structured objects, and
//   indexes them — so the same query is a bounded cursor read. Those four differences are
//   exactly what a chat thread needs.
//
// This is a cache, not a source of truth: the server always wins, and clearing it costs a
// re-fetch and nothing else.

const DB_NAME = 'telepati';
const DB_VERSION = 1;
const STORE_MESSAGES = 'messages';
const STORE_CHATS = 'chats';
const STORE_META = 'meta';

let dbPromise = null;

function openDatabase() {
  if (dbPromise) return dbPromise;

  dbPromise = new Promise((resolve, reject) => {
    const request = indexedDB.open(DB_NAME, DB_VERSION);

    request.onupgradeneeded = event => {
      const db = event.target.result;

      if (!db.objectStoreNames.contains(STORE_MESSAGES)) {
        const messages = db.createObjectStore(STORE_MESSAGES, { keyPath: 'id' });
        // Compound index so one cursor answers "newest N of this chat, before this time".
        messages.createIndex('chat_time', ['chatId', 'createdAt']);
      }

      if (!db.objectStoreNames.contains(STORE_CHATS)) {
        db.createObjectStore(STORE_CHATS, { keyPath: 'id' });
      }

      if (!db.objectStoreNames.contains(STORE_META)) {
        db.createObjectStore(STORE_META, { keyPath: 'key' });
      }
    };

    request.onsuccess = () => resolve(request.result);
    request.onerror = () => reject(request.error);
  });

  return dbPromise;
}

function transact(storeName, mode, work) {
  return openDatabase().then(db => new Promise((resolve, reject) => {
    const tx = db.transaction(storeName, mode);
    const store = tx.objectStore(storeName);
    let result;

    try {
      result = work(store);
    } catch (error) {
      reject(error);
      return;
    }

    tx.oncomplete = () => resolve(result);
    tx.onerror = () => reject(tx.error);
    tx.onabort = () => reject(tx.error);
  }));
}

window.telepatiStore = {
  get available() {
    return typeof indexedDB !== 'undefined';
  },

  /**
   * Newest-first page of a conversation, walked with a cursor so only the rows that are
   * returned are ever read — the store can hold a hundred thousand messages and this stays
   * the same cost.
   */
  async getMessages(chatId, take, beforeTicks) {
    if (!this.available) return [];

    const upper = beforeTicks && beforeTicks > 0 ? beforeTicks : Number.MAX_SAFE_INTEGER;
    const range = IDBKeyRange.bound([chatId, -Infinity], [chatId, upper], false, true);

    return transact(STORE_MESSAGES, 'readonly', store => {
      const results = [];
      const request = store.index('chat_time').openCursor(range, 'prev');

      request.onsuccess = event => {
        const cursor = event.target.result;
        if (!cursor || results.length >= take) return;

        results.push(cursor.value.payload);
        cursor.continue();
      };

      return results;
    }).then(rows => rows.reverse()); // chronological for rendering
  },

  async saveMessages(messages) {
    if (!this.available || !messages?.length) return;

    return transact(STORE_MESSAGES, 'readwrite', store => {
      for (const message of messages) {
        store.put({
          id: message.id,
          chatId: message.chatId,
          createdAt: Date.parse(message.createdAt),
          payload: message
        });
      }
    });
  },

  async deleteMessage(messageId) {
    if (!this.available) return;
    return transact(STORE_MESSAGES, 'readwrite', store => store.delete(messageId));
  },

  async getChats() {
    if (!this.available) return [];

    const rows = await transact(STORE_CHATS, 'readonly', store => {
      const results = [];
      const request = store.openCursor();

      request.onsuccess = event => {
        const cursor = event.target.result;
        if (!cursor) return;
        results.push(cursor.value.payload);
        cursor.continue();
      };

      return results;
    });

    return rows.sort((a, b) => {
      if (a.isPinned !== b.isPinned) return a.isPinned ? -1 : 1;
      return Date.parse(b.lastMessageAt ?? 0) - Date.parse(a.lastMessageAt ?? 0);
    });
  },

  async saveChats(chats) {
    if (!this.available || !chats?.length) return;

    return transact(STORE_CHATS, 'readwrite', store => {
      for (const chat of chats) store.put({ id: chat.id, payload: chat });
    });
  },

  async getMeta(key) {
    if (!this.available) return null;
    const row = await transact(STORE_META, 'readonly', store => {
      const request = store.get(key);
      let value = null;
      request.onsuccess = () => { value = request.result; };
      return { get current() { return value; } };
    });
    return row?.current?.value ?? null;
  },

  async setMeta(key, value) {
    if (!this.available) return;
    return transact(STORE_META, 'readwrite', store => store.put({ key, value }));
  },

  /** Keeps the store bounded: drop anything older than the cutoff. */
  async prune(maxAgeDays) {
    if (!this.available) return 0;

    const cutoff = Date.now() - maxAgeDays * 24 * 60 * 60 * 1000;

    return transact(STORE_MESSAGES, 'readwrite', store => {
      let removed = 0;
      const request = store.openCursor();

      request.onsuccess = event => {
        const cursor = event.target.result;
        if (!cursor) return;

        if (cursor.value.createdAt < cutoff) {
          cursor.delete();
          removed++;
        }
        cursor.continue();
      };

      return { get count() { return removed; } };
    }).then(r => r.count);
  },

  async clear() {
    if (!this.available) return;

    const db = await openDatabase();
    await Promise.all([STORE_MESSAGES, STORE_CHATS, STORE_META].map(name =>
      new Promise((resolve, reject) => {
        const tx = db.transaction(name, 'readwrite');
        tx.objectStore(name).clear();
        tx.oncomplete = resolve;
        tx.onerror = () => reject(tx.error);
      })));
  },

  async stats() {
    if (!this.available) return { messages: 0, chats: 0, bytes: 0 };

    const count = name => transact(name, 'readonly', store => {
      const request = store.count();
      let value = 0;
      request.onsuccess = () => { value = request.result; };
      return { get current() { return value; } };
    }).then(r => r.current);

    const [messages, chats] = await Promise.all([count(STORE_MESSAGES), count(STORE_CHATS)]);

    // navigator.storage gives an estimate for the whole origin, which is the closest thing
    // the browser exposes to a file size.
    let bytes = 0;
    if (navigator.storage?.estimate) {
      const estimate = await navigator.storage.estimate();
      bytes = estimate.usage ?? 0;
    }

    return { messages, chats, bytes };
  }
};

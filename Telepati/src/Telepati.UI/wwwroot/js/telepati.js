// Small interop surface for the Blazor apps. Everything here is DOM work Blazor
// cannot express on its own — nothing about the app's behaviour lives in this file.

window.telepati = {
  scrollToBottom(elementId) {
    const element = document.getElementById(elementId);
    if (!element) return;

    // Only auto-scroll when the reader is already near the bottom; yanking the
    // viewport away from someone reading scrollback is worse than a missed scroll.
    const distanceFromBottom = element.scrollHeight - element.scrollTop - element.clientHeight;
    if (distanceFromBottom > 220) return;

    requestAnimationFrame(() => {
      element.scrollTop = element.scrollHeight;
    });
  },

  scrollToMessage(messageId) {
    const element = document.getElementById(`msg-${messageId}`);
    if (element) element.scrollIntoView({ behavior: 'smooth', block: 'center' });
  },

  // --- infinite scroll ------------------------------------------------------
  _observers: new Map(),

  /**
   * Fires OnReachedTop when the sentinel scrolls into view. An IntersectionObserver is used
   * rather than a scroll handler because the browser evaluates it off the main thread and it
   * does not fire on every pixel of movement.
   */
  observeTop(sentinelId, containerId, dotNetRef) {
    const sentinel = document.getElementById(sentinelId);
    const container = document.getElementById(containerId);
    if (!sentinel || !container) return;

    this._observers.get(sentinelId)?.disconnect();

    const observer = new IntersectionObserver(entries => {
      for (const entry of entries) {
        if (entry.isIntersecting) dotNetRef.invokeMethodAsync('OnReachedTop');
      }
    }, {
      root: container,
      // Start loading slightly before the sentinel is actually visible, so the next page is
      // usually already there by the time the reader arrives.
      rootMargin: '240px 0px 0px 0px',
      threshold: 0
    });

    observer.observe(sentinel);
    this._observers.set(sentinelId, observer);
  },

  unobserve(sentinelId) {
    this._observers.get(sentinelId)?.disconnect();
    this._observers.delete(sentinelId);
  },

  /**
   * Keeps the reader where they were after older messages are prepended: measures the anchor's
   * position before the DOM grows and re-applies the same offset afterwards. Without this the
   * viewport jumps every time a page loads.
   */
  preserveScrollAt(containerId, anchorElementId) {
    const container = document.getElementById(containerId);
    const anchor = document.getElementById(anchorElementId);
    if (!container || !anchor) return;

    const offsetBefore = anchor.offsetTop - container.scrollTop;

    requestAnimationFrame(() => {
      const current = document.getElementById(anchorElementId);
      if (current) container.scrollTop = current.offsetTop - offsetBefore;
    });
  },

  prefersDark() {
    return window.matchMedia && window.matchMedia('(prefers-color-scheme: dark)').matches;
  },

  watchColorScheme(dotNetRef) {
    if (!window.matchMedia) return;

    const query = window.matchMedia('(prefers-color-scheme: dark)');
    query.addEventListener('change', e => dotNetRef.invokeMethodAsync('OnColorSchemeChanged', e.matches));
  },

  async requestNotificationPermission() {
    if (!('Notification' in window)) return 'unsupported';
    if (Notification.permission === 'granted') return 'granted';
    return await Notification.requestPermission();
  },

  notify(title, body, icon) {
    // A notification for the tab you are already looking at is just noise.
    if (!('Notification' in window) || Notification.permission !== 'granted') return;
    if (document.visibilityState === 'visible') return;

    new Notification(title, { body, icon, badge: icon });
  },

  playChime() {
    // Synthesised so the app ships no audio asset: two quick tones, deliberately soft.
    try {
      const context = new (window.AudioContext || window.webkitAudioContext)();
      const now = context.currentTime;

      [880, 1180].forEach((frequency, index) => {
        const oscillator = context.createOscillator();
        const gain = context.createGain();

        oscillator.type = 'sine';
        oscillator.frequency.value = frequency;
        gain.gain.setValueAtTime(0.0001, now + index * 0.09);
        gain.gain.exponentialRampToValueAtTime(0.11, now + index * 0.09 + 0.015);
        gain.gain.exponentialRampToValueAtTime(0.0001, now + index * 0.09 + 0.18);

        oscillator.connect(gain).connect(context.destination);
        oscillator.start(now + index * 0.09);
        oscillator.stop(now + index * 0.09 + 0.2);
      });

      setTimeout(() => context.close(), 600);
    } catch {
      // Audio is a nicety; a browser that blocks it must not break sending.
    }
  },

  autoGrow(textarea) {
    if (!textarea) return;
    textarea.style.height = 'auto';
    textarea.style.height = `${Math.min(textarea.scrollHeight, 136)}px`;
  },

  copyText(text) {
    return navigator.clipboard?.writeText(text);
  },

  // --- WebRTC ---------------------------------------------------------------
  // Media never touches the server; it only relays the handshake. This keeps the
  // peer connection and hands SDP/ICE back to .NET to forward over the transport.
  rtc: {
    peer: null,
    localStream: null,

    async start(iceServers, withVideo, dotNetRef) {
      this.peer = new RTCPeerConnection({ iceServers: iceServers.map(url => ({ urls: url })) });

      this.localStream = await navigator.mediaDevices.getUserMedia({ audio: true, video: withVideo });
      this.localStream.getTracks().forEach(track => this.peer.addTrack(track, this.localStream));

      const localVideo = document.getElementById('tp-local-video');
      if (localVideo) localVideo.srcObject = this.localStream;

      this.peer.ontrack = event => {
        const remoteVideo = document.getElementById('tp-remote-video');
        if (remoteVideo) remoteVideo.srcObject = event.streams[0];
      };

      this.peer.onicecandidate = event => {
        if (event.candidate) dotNetRef.invokeMethodAsync('OnIceCandidate', JSON.stringify(event.candidate));
      };

      const offer = await this.peer.createOffer();
      await this.peer.setLocalDescription(offer);
      return JSON.stringify(offer);
    },

    async accept(iceServers, withVideo, offerJson, dotNetRef) {
      await this.start(iceServers, withVideo, dotNetRef);

      await this.peer.setRemoteDescription(JSON.parse(offerJson));
      const answer = await this.peer.createAnswer();
      await this.peer.setLocalDescription(answer);
      return JSON.stringify(answer);
    },

    async applyAnswer(answerJson) {
      if (this.peer) await this.peer.setRemoteDescription(JSON.parse(answerJson));
    },

    async addIce(candidateJson) {
      if (this.peer) await this.peer.addIceCandidate(JSON.parse(candidateJson));
    },

    stop() {
      this.localStream?.getTracks().forEach(track => track.stop());
      this.peer?.close();
      this.peer = null;
      this.localStream = null;
    }
  }
};

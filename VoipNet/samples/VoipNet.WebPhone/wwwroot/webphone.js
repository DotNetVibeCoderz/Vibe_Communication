// A deliberately small SIP-over-WebSocket user agent (RFC 7118) for one outgoing call:
// INVITE with a complete (non-trickle) WebRTC offer, ACK, BYE, and answers to in-dialog requests.
// Everything else — ICE, DTLS-SRTP, codecs — is the browser's RTCPeerConnection talking to Voip.NET.

const token = (n = 10) => Array.from(crypto.getRandomValues(new Uint8Array(n)), b => (b % 36).toString(36)).join("");
const encoder = new TextEncoder();

let phone = null;

function build(startLine, headers, body = "") {
    const lines = [startLine, ...headers, `Content-Length: ${encoder.encode(body).length}`, "", body];
    return lines.join("\r\n");
}

function parse(text) {
    const split = text.indexOf("\r\n\r\n");
    const head = split < 0 ? text : text.slice(0, split);
    const body = split < 0 ? "" : text.slice(split + 4);
    const [first, ...rest] = head.split("\r\n");
    const headers = {};
    for (const line of rest) {
        const colon = line.indexOf(":");
        if (colon > 0) {
            const name = line.slice(0, colon).trim().toLowerCase();
            headers[name] ??= line.slice(colon + 1).trim();
        }
    }
    const status = /^SIP\/2\.0 (\d{3}) ?(.*)$/.exec(first);
    return status
        ? { response: true, code: +status[1], reason: status[2], headers, body }
        : { response: false, method: first.split(" ")[0], headers, body };
}

const uriOf = value => (/<([^>]+)>/.exec(value) ?? [null, value.split(";")[0]])[1];

function waitForIce(pc) {
    if (pc.iceGatheringState === "complete") return Promise.resolve();
    return new Promise(resolve => {
        const done = () => pc.iceGatheringState === "complete" && resolve();
        pc.addEventListener("icegatheringstatechange", done);
        setTimeout(resolve, 3000); // host candidates are enough on a local network
    });
}

async function collectStats(pc) {
    const out = { packetsSent: 0, packetsReceived: 0, packetsLost: 0, jitterMs: 0, micLevel: 0, remoteLevel: 0, codec: "", dtlsState: "", srtpCipher: "", candidatePair: "" };
    const report = await pc.getStats();
    const byId = new Map();
    report.forEach(s => byId.set(s.id, s));
    report.forEach(s => {
        if (s.type === "outbound-rtp" && s.kind === "audio") out.packetsSent = s.packetsSent ?? 0;
        if (s.type === "inbound-rtp" && s.kind === "audio") {
            out.packetsReceived = s.packetsReceived ?? 0;
            out.packetsLost = s.packetsLost ?? 0;
            out.jitterMs = Math.round((s.jitter ?? 0) * 10000) / 10;
            out.remoteLevel = s.audioLevel ?? 0;
            const codec = byId.get(s.codecId);
            if (codec) out.codec = `${codec.mimeType.replace("audio/", "")} ${codec.clockRate / 1000} kHz`;
        }
        if (s.type === "media-source" && s.kind === "audio") out.micLevel = s.audioLevel ?? 0;
        if (s.type === "transport") {
            out.dtlsState = s.dtlsState ?? "";
            out.srtpCipher = s.srtpCipher ?? "";
            const pair = byId.get(s.selectedCandidatePairId);
            if (pair) {
                const local = byId.get(pair.localCandidateId);
                const remote = byId.get(pair.remoteCandidateId);
                if (local && remote) out.candidatePair = `${local.address ?? local.ip}:${local.port} ⇄ ${remote.address ?? remote.ip}:${remote.port}`;
            }
        }
    });
    return out;
}

/** Places a call through the gateway. `view` receives OnPhase(phase, detail) and OnStats(stats). */
export async function call(view, wsUrl, desk, audioElement) {
    hangup();
    const host = new URL(wsUrl).host;
    const domain = `${token(8)}.invalid`;
    const state = {
        view, callId: token(20), fromTag: token(8), toTag: null, cseq: 1, remoteTarget: null,
        target: `sip:${desk}@${host};transport=ws`, ws: null, pc: null, stream: null, timer: null, ended: false,
    };
    phone = state;
    const report = (phase, detail = "") => view.invokeMethodAsync("OnPhase", phase, detail);

    const headers = (method, cseq, branch = `z9hG4bK${token(12)}`) => [
        `Via: SIP/2.0/WS ${domain};branch=${branch};rport`,
        "Max-Forwards: 70",
        `From: <sip:browser@${domain}>;tag=${state.fromTag}`,
        `To: <sip:${desk}@${host}>${state.toTag ? `;tag=${state.toTag}` : ""}`,
        `Call-ID: ${state.callId}`,
        `CSeq: ${cseq} ${method}`,
        `Contact: <sip:browser@${domain};transport=ws>`,
        "User-Agent: Voip.NET WebPhone sample",
    ];
    state.send = text => state.ws?.readyState === WebSocket.OPEN && state.ws.send(text);
    state.headers = headers;

    try {
        report("microphone");
        state.stream = await navigator.mediaDevices.getUserMedia({ audio: { echoCancellation: false, noiseSuppression: false } });
        report("connecting", wsUrl);
        state.ws = new WebSocket(wsUrl, "sip");
        await new Promise((resolve, reject) => {
            state.ws.onopen = resolve;
            state.ws.onerror = () => reject(new Error(`Could not open ${wsUrl}. Is the gateway running?`));
        });

        const pc = state.pc = new RTCPeerConnection({ bundlePolicy: "max-bundle" });
        state.stream.getTracks().forEach(t => pc.addTrack(t, state.stream));
        pc.ontrack = e => {
            audioElement.srcObject = e.streams[0] ?? new MediaStream([e.track]);
            audioElement.play().catch(() => { });
        };
        pc.onconnectionstatechange = () => report(`pc-${pc.connectionState}`);
        await pc.setLocalDescription(await pc.createOffer());
        await waitForIce(pc);

        state.ws.onmessage = e => onMessage(state, parse(e.data), report);
        state.ws.onclose = () => { if (!state.ended) finish(state, report, "signaling closed"); };
        state.send(build(`INVITE ${state.target} SIP/2.0`, [...headers("INVITE", state.cseq), "Content-Type: application/sdp"], pc.localDescription.sdp));
        report("inviting", desk);

        state.timer = setInterval(async () => {
            if (state.pc && !state.ended) view.invokeMethodAsync("OnStats", await collectStats(state.pc));
        }, 500);
    } catch (err) {
        finish(state, report, err.message ?? String(err), true);
    }
}

async function onMessage(state, msg, report) {
    if (msg.response) {
        if (!msg.headers.cseq?.endsWith("INVITE")) return;
        if (msg.code < 200) {
            report(msg.code === 180 ? "ringing" : "trying");
            return;
        }
        if (msg.code >= 300) {
            finish(state, report, `${msg.code} ${msg.reason}`, true);
            return;
        }
        state.toTag = /;tag=([^;>\s]+)/.exec(msg.headers.to)?.[1] ?? null;
        state.remoteTarget = uriOf(msg.headers.contact ?? state.target);
        state.send(build(`ACK ${state.remoteTarget} SIP/2.0`, state.headers("ACK", state.cseq)));
        await state.pc.setRemoteDescription({ type: "answer", sdp: msg.body });
        report("connected", msg.headers["user-agent"] ?? msg.headers.server ?? "");
        return;
    }

    // Requests inside the dialog: acknowledge with 200 OK, copying the transaction headers.
    const reply = build("SIP/2.0 200 OK", [
        `Via: ${msg.headers.via}`, `From: ${msg.headers.from}`, `To: ${msg.headers.to}`,
        `Call-ID: ${msg.headers["call-id"]}`, `CSeq: ${msg.headers.cseq}`,
    ]);
    if (msg.method !== "ACK") state.send(reply);
    if (msg.method === "BYE") finish(state, report, "the gateway hung up");
}

function finish(state, report, reason, failed = false) {
    if (state.ended) return;
    state.ended = true;
    clearInterval(state.timer);
    state.pc?.close();
    state.stream?.getTracks().forEach(t => t.stop());
    setTimeout(() => state.ws?.close(), 200);
    report(failed ? "failed" : "ended", reason);
    if (phone === state) phone = null;
}

/** Hangs up the current call, if any. */
export function hangup() {
    const state = phone;
    if (!state) return;
    if (state.toTag && !state.ended) {
        state.cseq += 1;
        state.send(build(`BYE ${state.remoteTarget} SIP/2.0`, state.headers("BYE", state.cseq)));
    }
    finish(state, (phase, detail) => state.view.invokeMethodAsync("OnPhase", phase, detail), "you hung up");
}

// A deliberately small SIP-over-WebSocket user agent (RFC 7118) for one outgoing call:
// INVITE with the WebRTC offer right away, ICE candidates trickled afterwards in INFO requests
// (RFC 8840), ACK, BYE, and answers to in-dialog requests.
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

async function collectStats(pc) {
    const out = {
        packetsSent: 0, packetsReceived: 0, packetsLost: 0, jitterMs: 0, micLevel: 0, remoteLevel: 0,
        codec: "", dtlsState: "", srtpCipher: "", candidatePair: "",
        videoCodec: "", videoFramesSent: 0, videoFramesReceived: 0, videoSize: "",
    };
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
        if (s.type === "outbound-rtp" && s.kind === "video") out.videoFramesSent = s.framesEncoded ?? 0;
        if (s.type === "inbound-rtp" && s.kind === "video") {
            out.videoFramesReceived = s.framesDecoded ?? 0;
            if (s.frameWidth) out.videoSize = `${s.frameWidth}×${s.frameHeight}`;
            const codec = byId.get(s.codecId);
            if (codec) out.videoCodec = codec.mimeType.replace("video/", "");
        }
        if (s.type === "media-source" && s.kind === "audio") out.micLevel = s.audioLevel ?? 0;
        if (s.type === "transport") {
            out.dtlsState = s.dtlsState ?? "";
            out.srtpCipher = s.srtpCipher ?? "";
            const pair = byId.get(s.selectedCandidatePairId);
            if (pair) {
                const local = byId.get(pair.localCandidateId);
                const remote = byId.get(pair.remoteCandidateId);
                if (local && remote) out.candidatePair = `${local.candidateType} ${local.address ?? local.ip}:${local.port} ⇄ ${remote.candidateType} ${remote.address ?? remote.ip}:${remote.port}`;
            }
        }
    });
    return out;
}

/** Places a call through the gateway. `view` receives OnPhase(phase, detail) and OnStats(stats). */
export async function call(view, wsUrl, desk, audioElement, videoElement, withVideo) {
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
        "Supported: trickle-ice",
        "User-Agent: Voip.NET WebPhone sample",
    ];
    state.send = text => state.ws?.readyState === WebSocket.OPEN && state.ws.send(text);
    state.headers = headers;

    try {
        report("microphone");
        // A small picture at a modest frame rate: this is a demonstration, not a broadcast.
        const wanted = { audio: { echoCancellation: false, noiseSuppression: false } };
        if (withVideo) wanted.video = { width: 320, height: 240, frameRate: 15 };
        state.stream = await navigator.mediaDevices.getUserMedia(wanted);
        report("connecting", wsUrl);
        state.ws = new WebSocket(wsUrl, "sip");
        await new Promise((resolve, reject) => {
            state.ws.onopen = resolve;
            state.ws.onerror = () => reject(new Error(`Could not open ${wsUrl}. Is the gateway running?`));
        });

        // Voip.NET gives each media stream its own port, so audio and video cannot share a transport:
        // with video the browser is asked for one transport per m-line.
        const pc = state.pc = new RTCPeerConnection({ bundlePolicy: withVideo ? "max-compat" : "max-bundle" });
        state.stream.getTracks().forEach(t => pc.addTrack(t, state.stream));
        pc.ontrack = e => {
            const element = e.track.kind === "video" ? videoElement : audioElement;
            if (!element) return;
            element.srcObject = e.streams[0] ?? new MediaStream([e.track]);
            element.play().catch(() => { });
        };
        pc.onconnectionstatechange = () => report(`pc-${pc.connectionState}`);
        // Candidates found before the call is answered wait here; later ones go out as they appear.
        state.pendingCandidates = [];
        pc.onicecandidate = e => {
            const line = e.candidate ? `a=${e.candidate.candidate}` : "a=end-of-candidates";
            if (state.toTag) trickle(state, [line]); else state.pendingCandidates.push(line);
        };
        await pc.setLocalDescription(await pc.createOffer());

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
        trickle(state, state.pendingCandidates.splice(0));
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

/** Sends ICE candidates in an INFO request with an SDP fragment (RFC 8840). */
function trickle(state, lines) {
    if (lines.length === 0 || state.ended) return;
    const sdp = state.pc.localDescription.sdp;
    const attribute = name => new RegExp(`a=${name}:(\\S+)`).exec(sdp)?.[1];
    const body = [
        `a=ice-ufrag:${attribute("ice-ufrag")}`, `a=ice-pwd:${attribute("ice-pwd")}`,
        "m=audio 9 UDP/TLS/RTP/SAVPF 0", `a=mid:${attribute("mid") ?? "0"}`, ...lines, "",
    ].join("\r\n");
    state.cseq += 1;
    state.send(build(`INFO ${state.remoteTarget} SIP/2.0`, [
        ...state.headers("INFO", state.cseq), "Info-Package: trickle-ice", "Content-Type: application/trickle-ice-sdpfrag",
    ], body));
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

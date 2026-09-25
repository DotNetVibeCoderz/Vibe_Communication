// Joining the room is one SIP call: INVITE over a WebSocket (RFC 7118) with the browser's offer,
// candidates trickled afterwards in INFO requests (RFC 8840), ACK, BYE, and 200 to anything in-dialog.
// Every participant dials the same room; the engine mixes the audio and forwards one camera at a time.

const token = (n = 10) => Array.from(crypto.getRandomValues(new Uint8Array(n)), b => (b % 36).toString(36)).join("");
const encoder = new TextEncoder();

let session = null;

function build(startLine, headers, body = "") {
    return [startLine, ...headers, `Content-Length: ${encoder.encode(body).length}`, "", body].join("\r\n");
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
    const out = { level: 0, videoIn: 0, videoOut: 0, size: "", codec: "", srtp: "", pair: "", pli: 0, keyframesOut: 0, allowance: 0 };
    const report = await pc.getStats();
    const byId = new Map();
    report.forEach(s => byId.set(s.id, s));
    report.forEach(s => {
        if (s.type === "media-source" && s.kind === "audio") out.level = s.audioLevel ?? 0;
        if (s.type === "outbound-rtp" && s.kind === "video") {
            out.videoOut = s.framesEncoded ?? 0;
            // How often the room asked for a keyframe, and how many the encoder made in answer.
            out.pli = s.pliCount ?? 0;
            out.keyframesOut = s.keyFramesEncoded ?? 0;
        }
        if (s.type === "inbound-rtp" && s.kind === "video") {
            out.videoIn = s.framesDecoded ?? 0;
            if (s.frameWidth) out.size = `${s.frameWidth}×${s.frameHeight}`;
            const codec = byId.get(s.codecId);
            if (codec) out.codec = codec.mimeType.replace("video/", "");
        }
        if (s.type === "transport") {
            out.srtp = s.srtpCipher ?? "";
            const pair = byId.get(s.selectedCandidatePairId);
            if (pair) {
                // What the browser thinks it may send, which is what it reads out of our REMB.
                out.allowance = Math.round((pair.availableOutgoingBitrate ?? 0) / 1000);
                const local = byId.get(pair.localCandidateId);
                const remote = byId.get(pair.remoteCandidateId);
                if (local && remote) out.pair = `${local.candidateType} ${local.address ?? local.ip}:${local.port} ⇄ ${remote.candidateType} ${remote.address ?? remote.ip}:${remote.port}`;
            }
        }
    });
    return out;
}

/** Joins the room. `view` receives OnPhase(phase, detail) and OnStats(stats). */
export async function join(view, wsUrl, name, localVideo, stageVideo, audioElement) {
    leave();
    const host = new URL(wsUrl).host;
    const domain = `${token(8)}.invalid`;
    const state = {
        view, callId: token(20), fromTag: token(8), toTag: null, cseq: 1, remoteTarget: null,
        // The name travels in the From URI, which is what the room puts on the roster.
        user: encodeURIComponent(name).replace(/%20/g, "+"),
        target: `sip:room@${host};transport=ws`, ws: null, pc: null, stream: null, timer: null, ended: false,
    };
    session = state;
    const report = (phase, detail = "") => view.invokeMethodAsync("OnPhase", phase, detail);

    const headers = (method, cseq, branch = `z9hG4bK${token(12)}`) => [
        `Via: SIP/2.0/WS ${domain};branch=${branch};rport`,
        "Max-Forwards: 70",
        `From: "${name.replace(/"/g, "")}" <sip:${state.user}@${domain}>;tag=${state.fromTag}`,
        `To: <sip:room@${host}>${state.toTag ? `;tag=${state.toTag}` : ""}`,
        `Call-ID: ${state.callId}`,
        `CSeq: ${cseq} ${method}`,
        `Contact: <sip:${state.user}@${domain};transport=ws>`,
        "Supported: trickle-ice",
        "User-Agent: Voip.NET meeting sample",
    ];
    state.send = text => state.ws?.readyState === WebSocket.OPEN && state.ws.send(text);
    state.headers = headers;

    try {
        report("devices");
        state.stream = await navigator.mediaDevices.getUserMedia({
            audio: { echoCancellation: true, noiseSuppression: true },
            video: { width: 320, height: 240, frameRate: 15 },
        });
        if (localVideo) {
            localVideo.srcObject = state.stream;
            localVideo.play().catch(() => { });
        }

        report("connecting", wsUrl);
        state.ws = new WebSocket(wsUrl, "sip");
        await new Promise((resolve, reject) => {
            state.ws.onopen = resolve;
            state.ws.onerror = () => reject(new Error(`Could not open ${wsUrl}. Is the room running?`));
        });

        // Audio and video get a transport each: the engine gives every stream its own port.
        const pc = state.pc = new RTCPeerConnection({ bundlePolicy: "max-compat" });
        state.stream.getTracks().forEach(t => pc.addTrack(t, state.stream));
        pc.ontrack = e => {
            const element = e.track.kind === "video" ? stageVideo : audioElement;
            if (!element) return;
            element.srcObject = e.streams[0] ?? new MediaStream([e.track]);
            element.play().catch(() => { });
        };
        pc.onconnectionstatechange = () => report(`pc-${pc.connectionState}`);
        state.pendingCandidates = [];
        pc.onicecandidate = e => {
            const line = e.candidate ? `a=${e.candidate.candidate}` : "a=end-of-candidates";
            if (state.toTag) trickle(state, [line]); else state.pendingCandidates.push(line);
        };
        await pc.setLocalDescription(await pc.createOffer());

        state.ws.onmessage = e => onMessage(state, parse(e.data), report);
        state.ws.onclose = () => { if (!state.ended) finish(state, report, "signaling closed"); };
        state.send(build(`INVITE ${state.target} SIP/2.0`, [...headers("INVITE", state.cseq), "Content-Type: application/sdp"], pc.localDescription.sdp));
        report("inviting", name);

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
        report("joined", msg.headers["user-agent"] ?? "");
        trickle(state, state.pendingCandidates.splice(0));
        return;
    }

    const reply = build("SIP/2.0 200 OK", [
        `Via: ${msg.headers.via}`, `From: ${msg.headers.from}`, `To: ${msg.headers.to}`,
        `Call-ID: ${msg.headers["call-id"]}`, `CSeq: ${msg.headers.cseq}`,
    ]);
    if (msg.method !== "ACK") state.send(reply);
    if (msg.method === "BYE") finish(state, report, "the room ended the call");
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
    report(failed ? "failed" : "left", reason);
    if (session === state) session = null;
}

/** Mutes or unmutes the microphone in the browser, so nothing is sent while muted. */
export function mute(on) {
    const track = session?.stream?.getAudioTracks()[0];
    if (!track) return false;
    track.enabled = !on;
    return on;
}

/** Leaves the meeting. */
export function leave() {
    const state = session;
    if (!state) return;
    if (state.toTag && !state.ended) {
        state.cseq += 1;
        state.send(build(`BYE ${state.remoteTarget} SIP/2.0`, state.headers("BYE", state.cseq)));
    }
    finish(state, (phase, detail) => state.view.invokeMethodAsync("OnPhase", phase, detail), "you left");
}

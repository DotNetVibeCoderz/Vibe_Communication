//! A full ICE agent (RFC 8445) for one component (RTP and RTCP multiplexed), sans-IO.
//!
//! All local candidates share one UDP socket, so pairs are formed from two local "bases": the
//! socket itself (host and server-reflexive candidates prune to it, RFC 8445 §6.1.2.4) and the TURN
//! relay when one is allocated. The agent paces connectivity checks, answers checks with triggered
//! checks, learns peer-reflexive candidates, resolves role conflicts, nominates (regular nomination
//! when controlling), and keeps consent fresh on the selected pair (RFC 7675).

use std::collections::HashMap;
use std::net::SocketAddr;
use std::time::{Duration, Instant};

use crate::stun::{self, Candidate, CandidateKind, StunMessage};

/// Pacing interval between new checks (RFC 8445 §14.2).
const TA: Duration = Duration::from_millis(50);
const RTO_INITIAL: Duration = Duration::from_millis(250);
const MAX_TRIES: u8 = 7;
const CONSENT_INTERVAL: Duration = Duration::from_secs(5);
const CONSENT_TIMEOUT: Duration = Duration::from_secs(30);
/// How long the controlling agent waits for better pairs after the first one succeeds.
const NOMINATION_WAIT: Duration = Duration::from_millis(400);
/// Give up when nothing has succeeded for this long and no check is pending.
const FAIL_AFTER: Duration = Duration::from_secs(12);

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum IceRole {
    Controlling,
    Controlled,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum IceConnectionState {
    New,
    Checking,
    Connected,
    Disconnected,
    Failed,
}

/// What the agent asks the session to do.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum IceOutput {
    /// Send a STUN message to `to`, through the TURN relay when `relay` is set.
    Send { to: SocketAddr, relay: bool, data: Vec<u8> },
    /// A pair was selected (or changed): send media to `remote`, through the relay when `relay` is set.
    Selected { remote: SocketAddr, relay: bool, local_kind: &'static str, remote_kind: &'static str },
    Disconnected,
    Failed,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum PairState {
    Waiting,
    InProgress,
    Succeeded,
    Failed,
}

#[derive(Debug, Clone)]
struct Pair {
    relay: bool,
    remote: SocketAddr,
    local_priority: u32,
    remote_priority: u32,
    remote_kind: CandidateKind,
    state: PairState,
    tries: u8,
    next_send: Instant,
    rto: Duration,
    /// The check carries USE-CANDIDATE (controlling side nominating this pair).
    nominating: bool,
    /// The controlled side saw USE-CANDIDATE for this pair.
    nominated: bool,
}

impl Pair {
    fn priority(&self, role: IceRole) -> u64 {
        let (g, d) = match role {
            IceRole::Controlling => (self.local_priority as u64, self.remote_priority as u64),
            IceRole::Controlled => (self.remote_priority as u64, self.local_priority as u64),
        };
        (1u64 << 32) * g.min(d) + 2 * g.max(d) + u64::from(g > d)
    }
}

enum Pending {
    Check(usize),
    Consent,
}

fn kind_name(kind: CandidateKind) -> &'static str {
    match kind {
        CandidateKind::Host => "host",
        CandidateKind::ServerReflexive => "srflx",
        CandidateKind::Relayed => "relay",
        CandidateKind::PeerReflexive => "prflx",
    }
}

pub struct IceAgent {
    local_ufrag: String,
    local_pwd: String,
    remote_ufrag: Option<String>,
    remote_pwd: Option<String>,
    role: IceRole,
    tie_breaker: u64,
    /// Priority of the best direct (host) candidate, and of the relay candidate when allocated.
    direct_priority: u32,
    relay_priority: Option<u32>,
    ipv4: bool,
    remote: Vec<Candidate>,
    pairs: Vec<Pair>,
    transactions: HashMap<[u8; 12], Pending>,
    selected: Option<usize>,
    state: IceConnectionState,
    next_pace: Instant,
    started: Option<Instant>,
    first_success: Option<Instant>,
    last_consent: Instant,
    next_consent: Instant,
}

impl IceAgent {
    pub fn new(local: &[Candidate], role: IceRole) -> Self {
        let direct_priority = local.iter().filter(|c| c.kind != CandidateKind::Relayed).map(|c| c.priority).max().unwrap_or(0);
        let relay_priority = local.iter().find(|c| c.kind == CandidateKind::Relayed).map(|c| c.priority);
        let now = Instant::now();
        Self {
            local_ufrag: crate::sip::message::random_token(8),
            local_pwd: crate::sip::message::random_token(24),
            remote_ufrag: None,
            remote_pwd: None,
            role,
            tie_breaker: rand::random(),
            direct_priority,
            relay_priority,
            ipv4: local.iter().find(|c| c.kind == CandidateKind::Host).is_none_or(|c| c.address.is_ipv4()),
            remote: Vec::new(),
            pairs: Vec::new(),
            transactions: HashMap::new(),
            selected: None,
            state: IceConnectionState::New,
            next_pace: now,
            started: None,
            first_success: None,
            last_consent: now,
            next_consent: now,
        }
    }

    pub fn credentials(&self) -> (&str, &str) {
        (&self.local_ufrag, &self.local_pwd)
    }

    pub fn remote_ufrag(&self) -> Option<&str> {
        self.remote_ufrag.as_deref()
    }

    pub fn role(&self) -> IceRole {
        self.role
    }

    pub fn state(&self) -> IceConnectionState {
        self.state
    }

    pub fn is_connected(&self) -> bool {
        self.state == IceConnectionState::Connected
    }

    /// Starts over with fresh local credentials (ICE restart, RFC 8445 §9).
    pub fn restart(&mut self) {
        self.local_ufrag = crate::sip::message::random_token(8);
        self.local_pwd = crate::sip::message::random_token(24);
        self.remote_ufrag = None;
        self.remote_pwd = None;
        self.remote.clear();
        self.pairs.clear();
        self.transactions.clear();
        self.selected = None;
        self.started = None;
        self.first_success = None;
        // Media keeps flowing on the old pair until a new one is selected.
        if self.state != IceConnectionState::Connected {
            self.state = IceConnectionState::New;
        }
    }

    /// Applies the peer's credentials and candidates from SDP.
    pub fn set_remote(&mut self, ufrag: &str, pwd: &str, candidates: &[Candidate], role: IceRole, now: Instant) {
        if self.remote_ufrag.as_deref().is_some_and(|u| u != ufrag) {
            // The peer restarted ICE without us noticing first.
            let (u, p) = (self.local_ufrag.clone(), self.local_pwd.clone());
            self.restart();
            (self.local_ufrag, self.local_pwd) = (u, p);
        }
        if self.remote_ufrag.is_none() {
            self.role = role;
        }
        self.remote_ufrag = Some(ufrag.to_owned());
        self.remote_pwd = Some(pwd.to_owned());
        for c in candidates {
            self.add_remote_candidate(c.clone(), now);
        }
        if self.started.is_none() {
            self.started = Some(now);
            if self.state == IceConnectionState::New {
                self.state = IceConnectionState::Checking;
            }
        }
    }

    /// Adds a candidate learned later (trickle ICE, RFC 8838).
    pub fn add_remote_candidate(&mut self, c: Candidate, now: Instant) {
        if c.component != 1 || self.remote.iter().any(|r| r.address == c.address) {
            return;
        }
        let bases = std::iter::once((false, self.direct_priority)).chain(self.relay_priority.map(|p| (true, p)));
        for (relay, local_priority) in bases {
            // The socket only reaches its own address family; the relay can reach both.
            if !relay && c.address.is_ipv4() != self.ipv4 {
                continue;
            }
            self.pairs.push(Pair {
                relay,
                remote: c.address,
                local_priority,
                remote_priority: c.priority,
                remote_kind: c.kind,
                state: PairState::Waiting,
                tries: 0,
                next_send: now,
                rto: RTO_INITIAL,
                nominating: false,
                nominated: false,
            });
        }
        self.remote.push(c);
    }

    fn username_for_checks(&self) -> Option<(String, String)> {
        Some((format!("{}:{}", self.remote_ufrag.as_ref()?, self.local_ufrag), self.remote_pwd.clone()?))
    }

    fn check_message(&mut self, pair: usize, username: &str, pwd: &str, pending: Pending) -> IceOutput {
        let p = &self.pairs[pair];
        let mut req = StunMessage::new(stun::BINDING_REQUEST);
        // PRIORITY is what a peer-reflexive candidate learned from this check would get.
        let prflx_priority = (110u32 << 24) | (p.local_priority & 0x00FF_FFFF);
        req.add(stun::ATTR_USERNAME, username.as_bytes().to_vec()).add(stun::ATTR_PRIORITY, prflx_priority.to_be_bytes().to_vec());
        match self.role {
            IceRole::Controlling => {
                req.add(stun::ATTR_ICE_CONTROLLING, self.tie_breaker.to_be_bytes().to_vec());
                if p.nominating {
                    req.add(stun::ATTR_USE_CANDIDATE, Vec::new());
                }
            }
            IceRole::Controlled => {
                req.add(stun::ATTR_ICE_CONTROLLED, self.tie_breaker.to_be_bytes().to_vec());
            }
        }
        let out = IceOutput::Send { to: p.remote, relay: p.relay, data: req.encode(Some(pwd.as_bytes()), true) };
        self.transactions.insert(req.transaction_id, pending);
        out
    }

    /// Drives pacing, retransmissions, nomination, consent and failure detection.
    pub fn poll(&mut self, now: Instant) -> Vec<IceOutput> {
        let mut out = Vec::new();
        let Some((username, pwd)) = self.username_for_checks() else { return out };

        // Retransmit checks in progress.
        for i in 0..self.pairs.len() {
            let p = &mut self.pairs[i];
            if p.state == PairState::InProgress && now >= p.next_send {
                if p.tries >= MAX_TRIES {
                    p.state = PairState::Failed;
                    p.nominating = false;
                    continue;
                }
                p.tries += 1;
                p.next_send = now + p.rto;
                p.rto = (p.rto * 2).min(Duration::from_millis(1600));
                out.push(self.check_message(i, &username, &pwd, Pending::Check(i)));
            }
        }

        // One new (or triggered) check per Ta, highest priority first.
        if now >= self.next_pace {
            let role = self.role;
            let next = (0..self.pairs.len())
                .filter(|&i| self.pairs[i].state == PairState::Waiting && now >= self.pairs[i].next_send)
                .max_by_key(|&i| self.pairs[i].priority(role));
            if let Some(i) = next {
                let p = &mut self.pairs[i];
                p.state = PairState::InProgress;
                p.tries = 1;
                p.rto = RTO_INITIAL;
                p.next_send = now + p.rto;
                self.next_pace = now + TA;
                out.push(self.check_message(i, &username, &pwd, Pending::Check(i)));
            }
        }

        // Regular nomination: pick the best valid pair once better pairs had their chance.
        if self.role == IceRole::Controlling && self.selected.is_none() && !self.pairs.iter().any(|p| p.nominating) {
            let role = self.role;
            if let Some(best) = (0..self.pairs.len()).filter(|&i| self.pairs[i].state == PairState::Succeeded).max_by_key(|&i| self.pairs[i].priority(role)) {
                let best_priority = self.pairs[best].priority(role);
                let better_pending = self.pairs.iter().any(|p| matches!(p.state, PairState::Waiting | PairState::InProgress) && p.priority(role) > best_priority);
                let waited = self.first_success.is_some_and(|t| now.duration_since(t) >= NOMINATION_WAIT);
                if !better_pending || waited {
                    let p = &mut self.pairs[best];
                    p.nominating = true;
                    p.state = PairState::InProgress;
                    p.tries = 1;
                    p.rto = RTO_INITIAL;
                    p.next_send = now + p.rto;
                    out.push(self.check_message(best, &username, &pwd, Pending::Check(best)));
                }
            }
        }

        // Consent freshness on the selected pair.
        if let Some(sel) = self.selected {
            if now >= self.next_consent {
                let jitter = Duration::from_millis(rand::random::<u64>() % 2000);
                self.next_consent = now + CONSENT_INTERVAL - Duration::from_millis(1000) + jitter;
                out.push(self.check_message(sel, &username, &pwd, Pending::Consent));
            }
            if self.state == IceConnectionState::Connected && now.duration_since(self.last_consent) > CONSENT_TIMEOUT {
                self.state = IceConnectionState::Disconnected;
                out.push(IceOutput::Disconnected);
            }
        } else if self.state == IceConnectionState::Checking {
            let all_done = !self.pairs.is_empty() && self.pairs.iter().all(|p| p.state == PairState::Failed);
            let too_long = self.started.is_some_and(|t| now.duration_since(t) > FAIL_AFTER);
            if all_done && too_long {
                self.state = IceConnectionState::Failed;
                out.push(IceOutput::Failed);
            }
        }
        out
    }

    /// Handles a STUN binding request. Returns the response plus any outputs (selection).
    pub fn handle_request(&mut self, raw: &[u8], msg: &StunMessage, from: SocketAddr, relay: bool, now: Instant) -> Vec<IceOutput> {
        let mut out = Vec::new();
        let Some(user) = msg.get(stun::ATTR_USERNAME).and_then(|u| std::str::from_utf8(u).ok()) else { return out };
        if user.split(':').next() != Some(self.local_ufrag.as_str()) || !StunMessage::verify_integrity(raw, self.local_pwd.as_bytes()) {
            return out;
        }

        // Role conflicts (RFC 8445 §7.3.1.1).
        let theirs = |attr| msg.get(attr).and_then(|v| v.try_into().ok()).map(u64::from_be_bytes);
        if let Some(t) = theirs(stun::ATTR_ICE_CONTROLLING).filter(|_| self.role == IceRole::Controlling) {
            if self.tie_breaker >= t {
                out.push(self.error_response(msg, from, relay, 487));
                return out;
            }
            self.switch_role(IceRole::Controlled);
        } else if let Some(t) = theirs(stun::ATTR_ICE_CONTROLLED).filter(|_| self.role == IceRole::Controlled) {
            if self.tie_breaker >= t {
                self.switch_role(IceRole::Controlling);
            } else {
                out.push(self.error_response(msg, from, relay, 487));
                return out;
            }
        }

        let mut resp = msg.reply(stun::BINDING_SUCCESS);
        resp.add_xor_address(stun::ATTR_XOR_MAPPED_ADDRESS, from);
        out.push(IceOutput::Send { to: from, relay, data: resp.encode(Some(self.local_pwd.as_bytes()), true) });

        // Checks can arrive before the answer carrying the peer's credentials.
        if self.remote_pwd.is_none() {
            return out;
        }

        // Peer-reflexive remote candidate (RFC 8445 §7.3.1.3).
        if !self.remote.iter().any(|c| c.address == from) {
            let priority = msg.get(stun::ATTR_PRIORITY).and_then(|v| v.try_into().ok()).map(u32::from_be_bytes).unwrap_or(0);
            let mut c = Candidate::new(CandidateKind::PeerReflexive, from, 1);
            c.priority = priority;
            self.add_remote_candidate(c, now);
        }

        let Some(i) = self.pairs.iter().position(|p| p.remote == from && p.relay == relay) else { return out };
        let use_candidate = msg.get(stun::ATTR_USE_CANDIDATE).is_some();
        let p = &mut self.pairs[i];
        if use_candidate && self.role == IceRole::Controlled {
            p.nominated = true;
        }
        match p.state {
            PairState::Succeeded => {
                if p.nominated && self.selected != Some(i) {
                    out.extend(self.select(i, now));
                }
            }
            PairState::InProgress => {}
            // Triggered check: jump the queue.
            PairState::Waiting | PairState::Failed => {
                p.state = PairState::Waiting;
                p.tries = 0;
                p.next_send = now;
                self.next_pace = now;
            }
        }
        out
    }

    fn error_response(&self, msg: &StunMessage, to: SocketAddr, relay: bool, code: u16) -> IceOutput {
        let mut resp = msg.reply(stun::BINDING_ERROR);
        let mut value = vec![0, 0, (code / 100) as u8, (code % 100) as u8];
        value.extend_from_slice(b"Role Conflict");
        resp.add(stun::ATTR_ERROR_CODE, value);
        IceOutput::Send { to, relay, data: resp.encode(Some(self.local_pwd.as_bytes()), true) }
    }

    fn switch_role(&mut self, role: IceRole) {
        self.role = role;
        for p in &mut self.pairs {
            p.nominating = false;
        }
    }

    /// Handles a binding success or error response. Returns true when it belonged to this agent.
    pub fn handle_response(&mut self, raw: &[u8], msg: &StunMessage, from: SocketAddr, now: Instant) -> (bool, Vec<IceOutput>) {
        let mut out = Vec::new();
        let Some(pending) = self.transactions.remove(&msg.transaction_id) else { return (false, out) };
        let valid = self.remote_pwd.as_ref().is_some_and(|p| StunMessage::verify_integrity(raw, p.as_bytes()));
        match pending {
            Pending::Consent => {
                if valid && msg.msg_type == stun::BINDING_SUCCESS {
                    self.last_consent = now;
                    if self.state == IceConnectionState::Disconnected {
                        self.state = IceConnectionState::Connected;
                    }
                }
            }
            Pending::Check(i) => {
                let Some(p) = self.pairs.get_mut(i) else { return (true, out) };
                if msg.msg_type == stun::BINDING_ERROR {
                    if msg.error_code() == Some(487) {
                        let flipped = if self.role == IceRole::Controlling { IceRole::Controlled } else { IceRole::Controlling };
                        self.switch_role(flipped);
                        let p = &mut self.pairs[i];
                        p.state = PairState::Waiting;
                        p.next_send = now;
                    } else {
                        p.state = PairState::Failed;
                    }
                    return (true, out);
                }
                // A response must come from where the check went (symmetry, RFC 8445 §7.2.5.2.1).
                if !valid || from != p.remote {
                    return (true, out);
                }
                p.state = PairState::Succeeded;
                self.first_success.get_or_insert(now);
                let nominate = (p.nominating && self.role == IceRole::Controlling) || (p.nominated && self.role == IceRole::Controlled);
                if nominate && self.selected != Some(i) {
                    out.extend(self.select(i, now));
                }
            }
        }
        (true, out)
    }

    fn select(&mut self, i: usize, now: Instant) -> Vec<IceOutput> {
        self.selected = Some(i);
        self.state = IceConnectionState::Connected;
        self.last_consent = now;
        self.next_consent = now + CONSENT_INTERVAL;
        let p = &self.pairs[i];
        vec![IceOutput::Selected {
            remote: p.remote,
            relay: p.relay,
            local_kind: if p.relay { "relay" } else { "host" },
            remote_kind: kind_name(p.remote_kind),
        }]
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn addr(s: &str) -> SocketAddr {
        s.parse().unwrap()
    }

    struct Peer {
        agent: IceAgent,
        addr: SocketAddr,
        selected: Option<SocketAddr>,
        events: Vec<IceOutput>,
    }

    impl Peer {
        fn new(address: &str, role: IceRole) -> Self {
            let addr = addr(address);
            Self { agent: IceAgent::new(&[Candidate::new(CandidateKind::Host, addr, 1)], role), addr, selected: None, events: Vec::new() }
        }
    }

    /// Delivers packets between two agents; `blocked` addresses drop everything sent to them.
    fn run(a: &mut Peer, b: &mut Peer, blocked: &[SocketAddr], ms: u64) {
        let start = Instant::now();
        let mut queue: Vec<(bool, SocketAddr, Vec<u8>)> = Vec::new();
        for step in 0..ms / 10 {
            let now = start + Duration::from_millis(step * 10);
            for (to_b, from, data) in std::mem::take(&mut queue) {
                let (target, source_addr) = if to_b { (&mut *b, from) } else { (&mut *a, from) };
                let msg = StunMessage::decode(&data).unwrap();
                let outs = if msg.msg_type == stun::BINDING_REQUEST {
                    target.agent.handle_request(&data, &msg, source_addr, false, now)
                } else {
                    target.agent.handle_response(&data, &msg, source_addr, now).1
                };
                let self_addr = target.addr;
                for o in outs {
                    route(o, to_b, self_addr, target, &mut queue, blocked);
                }
            }
            for (peer, is_b) in [(&mut *a, false), (&mut *b, true)] {
                let self_addr = peer.addr;
                for o in peer.agent.poll(now) {
                    route(o, is_b, self_addr, peer, &mut queue, blocked);
                }
            }
        }

        fn route(o: IceOutput, from_b: bool, self_addr: SocketAddr, peer: &mut Peer, queue: &mut Vec<(bool, SocketAddr, Vec<u8>)>, blocked: &[SocketAddr]) {
            match o {
                IceOutput::Send { to, data, .. } => {
                    if !blocked.contains(&to) {
                        // Messages from b go to a (to_b = false) and vice versa.
                        queue.push((!from_b, self_addr, data));
                    }
                }
                IceOutput::Selected { remote, .. } => peer.selected = Some(remote),
                other => peer.events.push(other),
            }
        }
    }

    fn exchange(a: &mut Peer, b: &mut Peer, a_candidates: bool) {
        let now = Instant::now();
        let (au, ap) = (a.agent.local_ufrag.clone(), a.agent.local_pwd.clone());
        let (bu, bp) = (b.agent.local_ufrag.clone(), b.agent.local_pwd.clone());
        let a_cands = if a_candidates { vec![Candidate::new(CandidateKind::Host, a.addr, 1)] } else { vec![] };
        let (ra, rb) = (a.agent.role, b.agent.role);
        b.agent.set_remote(&au, &ap, &a_cands, rb, now);
        a.agent.set_remote(&bu, &bp, &[Candidate::new(CandidateKind::Host, b.addr, 1)], ra, now);
    }

    #[test]
    fn controlling_nominates_and_both_select_the_pair() {
        let mut a = Peer::new("10.0.0.1:4000", IceRole::Controlling);
        let mut b = Peer::new("10.0.0.2:5000", IceRole::Controlled);
        exchange(&mut a, &mut b, true);
        run(&mut a, &mut b, &[], 2000);
        assert_eq!(a.selected, Some(b.addr));
        assert_eq!(b.selected, Some(a.addr));
        assert!(a.agent.is_connected() && b.agent.is_connected());
    }

    #[test]
    fn learns_peer_reflexive_candidate_without_signaled_candidates() {
        let mut a = Peer::new("10.0.0.1:4000", IceRole::Controlling);
        let mut b = Peer::new("10.0.0.2:5000", IceRole::Controlled);
        exchange(&mut a, &mut b, false); // b does not know any candidate of a (e.g. mDNS names)
        run(&mut a, &mut b, &[], 2000);
        assert_eq!(b.selected, Some(a.addr));
        assert!(b.agent.remote.iter().any(|c| c.kind == CandidateKind::PeerReflexive));
    }

    #[test]
    fn role_conflict_is_resolved() {
        let mut a = Peer::new("10.0.0.1:4000", IceRole::Controlling);
        let mut b = Peer::new("10.0.0.2:5000", IceRole::Controlling);
        exchange(&mut a, &mut b, true);
        run(&mut a, &mut b, &[], 3000);
        assert_ne!(a.agent.role(), b.agent.role());
        assert_eq!(a.selected, Some(b.addr));
        assert_eq!(b.selected, Some(a.addr));
    }

    #[test]
    fn fails_when_nothing_answers_and_prefers_reachable_candidates() {
        let mut a = Peer::new("10.0.0.1:4000", IceRole::Controlling);
        let mut b = Peer::new("10.0.0.2:5000", IceRole::Controlled);
        exchange(&mut a, &mut b, true);
        let blocked = [b.addr, a.addr];
        run(&mut a, &mut b, &blocked, 14_000);
        assert!(a.events.contains(&IceOutput::Failed));
        assert_eq!(a.selected, None);
    }

    #[test]
    fn restart_keeps_media_and_reconnects() {
        let mut a = Peer::new("10.0.0.1:4000", IceRole::Controlling);
        let mut b = Peer::new("10.0.0.2:5000", IceRole::Controlled);
        exchange(&mut a, &mut b, true);
        run(&mut a, &mut b, &[], 1500);
        a.agent.restart();
        b.agent.restart();
        a.selected = None;
        b.selected = None;
        exchange(&mut a, &mut b, true);
        run(&mut a, &mut b, &[], 1500);
        assert_eq!(a.selected, Some(b.addr));
        assert_eq!(b.selected, Some(a.addr));
    }

    #[test]
    fn consent_expires_when_the_peer_goes_silent() {
        let mut a = Peer::new("10.0.0.1:4000", IceRole::Controlling);
        let mut b = Peer::new("10.0.0.2:5000", IceRole::Controlled);
        exchange(&mut a, &mut b, true);
        run(&mut a, &mut b, &[], 1500);
        assert!(a.agent.is_connected());
        // Rewind the consent clock instead of simulating 30 idle seconds.
        a.agent.last_consent = Instant::now() - CONSENT_TIMEOUT - Duration::from_secs(1);
        let outs = a.agent.poll(Instant::now());
        assert!(outs.contains(&IceOutput::Disconnected));
    }
}

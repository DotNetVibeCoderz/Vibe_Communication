//! Floor control (BFCP, RFC 4582 with the RFC 8855 header), sans IO.
//!
//! A floor is permission to send one of a conference's streams — in practice the shared screen. One
//! endpoint holds the floor and everybody else asks for it, which is what keeps two people from
//! presenting over each other on equipment that expects to be asked (Polycom and Cisco rooms will
//! not share a screen without it).
//!
//! This module knows the messages and the state; the endpoint gives it a socket and a clock.

use std::collections::HashMap;
use std::time::{Duration, Instant};

/// Messages this implementation speaks. The rest of RFC 4582 is parsed far enough to be ignored.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Primitive {
    FloorRequest = 1,
    FloorRelease = 2,
    FloorRequestQuery = 3,
    FloorRequestStatus = 4,
    Hello = 11,
    HelloAck = 12,
    Error = 13,
    FloorRequestStatusAck = 14,
    Goodbye = 16,
    GoodbyeAck = 17,
}

impl Primitive {
    fn from_u8(value: u8) -> Option<Self> {
        Some(match value {
            1 => Self::FloorRequest,
            2 => Self::FloorRelease,
            3 => Self::FloorRequestQuery,
            4 => Self::FloorRequestStatus,
            11 => Self::Hello,
            12 => Self::HelloAck,
            13 => Self::Error,
            14 => Self::FloorRequestStatusAck,
            16 => Self::Goodbye,
            17 => Self::GoodbyeAck,
            _ => return None,
        })
    }
}

/// Where a request has got to (RFC 4582 §5.2.5).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum RequestStatus {
    Pending = 1,
    Accepted = 2,
    Granted = 3,
    Denied = 4,
    Cancelled = 5,
    Released = 6,
    Revoked = 7,
}

impl RequestStatus {
    fn from_u8(value: u8) -> Option<Self> {
        Some(match value {
            1 => Self::Pending,
            2 => Self::Accepted,
            3 => Self::Granted,
            4 => Self::Denied,
            5 => Self::Cancelled,
            6 => Self::Released,
            7 => Self::Revoked,
            _ => return None,
        })
    }
}

/// Attribute types used here. Grouped attributes carry other attributes inside them.
const ATTR_BENEFICIARY_ID: u8 = 1;
const ATTR_FLOOR_ID: u8 = 2;
const ATTR_FLOOR_REQUEST_ID: u8 = 3;
const ATTR_REQUEST_STATUS: u8 = 5;
const ATTR_ERROR_CODE: u8 = 6;
const ATTR_FLOOR_REQUEST_INFORMATION: u8 = 12;
const ATTR_OVERALL_REQUEST_STATUS: u8 = 14;
const ATTR_FLOOR_REQUEST_STATUS: u8 = 15;

/// One message: the header fields and the few attributes this implementation reads.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Message {
    pub primitive: Primitive,
    pub conference: u32,
    pub transaction: u16,
    pub user: u16,
    /// Floors named by the message, in the order they appeared.
    pub floors: Vec<u16>,
    /// The request this message is about, when it names one.
    pub request_id: Option<u16>,
    /// Status of that request, for a FloorRequestStatus.
    pub status: Option<RequestStatus>,
    /// Error code, for an Error.
    pub error: Option<u8>,
}

impl Message {
    /// A message with nothing but its header fields set.
    pub fn new(primitive: Primitive, conference: u32, transaction: u16, user: u16) -> Self {
        Self { primitive, conference, transaction, user, floors: Vec::new(), request_id: None, status: None, error: None }
    }

    /// Writes the message, header and attributes, as it goes on the wire.
    pub fn encode(&self) -> Vec<u8> {
        let mut payload = Vec::new();
        for floor in &self.floors {
            // A FloorRequest names the floors it wants; a status names them inside its information.
            if self.primitive != Primitive::FloorRequestStatus {
                push_u16_attribute(&mut payload, ATTR_FLOOR_ID, *floor);
            }
        }

        if self.primitive == Primitive::FloorRequestStatus {
            let mut information = Vec::new();
            push_u16_attribute(&mut information, ATTR_FLOOR_REQUEST_ID, self.request_id.unwrap_or(0));
            if let Some(status) = self.status {
                let mut overall = Vec::new();
                push_u16_attribute(&mut overall, ATTR_FLOOR_REQUEST_ID, self.request_id.unwrap_or(0));
                push_status_attribute(&mut overall, status);
                push_grouped(&mut information, ATTR_OVERALL_REQUEST_STATUS, &overall);
            }

            for floor in &self.floors {
                let mut per_floor = Vec::new();
                push_u16_attribute(&mut per_floor, ATTR_FLOOR_ID, *floor);
                if let Some(status) = self.status {
                    push_status_attribute(&mut per_floor, status);
                }

                push_grouped(&mut information, ATTR_FLOOR_REQUEST_STATUS, &per_floor);
            }

            push_grouped(&mut payload, ATTR_FLOOR_REQUEST_INFORMATION, &information);
        } else if let Some(request) = self.request_id {
            push_u16_attribute(&mut payload, ATTR_FLOOR_REQUEST_ID, request);
        }

        if let Some(error) = self.error {
            push_u16_attribute(&mut payload, ATTR_ERROR_CODE, u16::from(error));
        }

        let mut out = Vec::with_capacity(12 + payload.len());
        // Version 2, no fragmentation, no response needed: the transaction id already pairs them up.
        out.push(0x80);
        out.push(self.primitive as u8);
        out.extend_from_slice(&((payload.len() / 4) as u16).to_be_bytes());
        out.extend_from_slice(&self.conference.to_be_bytes());
        out.extend_from_slice(&self.transaction.to_be_bytes());
        out.extend_from_slice(&self.user.to_be_bytes());
        out.extend_from_slice(&payload);
        out
    }

    /// Reads a message, or nothing when the bytes are not one.
    pub fn decode(data: &[u8]) -> Option<Self> {
        if data.len() < 12 || data[0] >> 6 == 0 {
            return None;
        }

        let primitive = Primitive::from_u8(data[1])?;
        let words = u16::from_be_bytes([data[2], data[3]]) as usize;
        let end = 12 + (words * 4);
        if end > data.len() {
            return None;
        }

        let mut message = Self::new(
            primitive,
            u32::from_be_bytes([data[4], data[5], data[6], data[7]]),
            u16::from_be_bytes([data[8], data[9]]),
            u16::from_be_bytes([data[10], data[11]]),
        );
        message.read_attributes(&data[12..end]);
        Some(message)
    }

    /// Walks a run of attributes, stepping into the grouped ones.
    fn read_attributes(&mut self, mut data: &[u8]) {
        while data.len() >= 2 {
            let kind = data[0] >> 1;
            let length = data[1] as usize;
            if length < 2 || length > data.len() {
                return;
            }

            let value = &data[2..length];
            match kind {
                ATTR_FLOOR_ID if value.len() >= 2 => self.floors.push(u16::from_be_bytes([value[0], value[1]])),
                ATTR_FLOOR_REQUEST_ID if value.len() >= 2 => {
                    self.request_id.get_or_insert(u16::from_be_bytes([value[0], value[1]]));
                }
                ATTR_BENEFICIARY_ID => {}
                ATTR_REQUEST_STATUS if !value.is_empty() => {
                    if let Some(status) = RequestStatus::from_u8(value[0]) {
                        self.status = Some(status);
                    }
                }
                ATTR_ERROR_CODE if !value.is_empty() => self.error = Some(value[0]),
                ATTR_FLOOR_REQUEST_INFORMATION | ATTR_OVERALL_REQUEST_STATUS | ATTR_FLOOR_REQUEST_STATUS => {
                    // Grouped: the first two bytes of the value are the request or floor id it is
                    // about, and the attributes inside follow.
                    if value.len() > 2 {
                        if kind == ATTR_FLOOR_REQUEST_INFORMATION {
                            self.request_id.get_or_insert(u16::from_be_bytes([value[0], value[1]]));
                        }

                        self.read_attributes(&value[2..]);
                    }
                }
                _ => {}
            }

            // Attributes are padded out to a four-byte boundary.
            let step = length.div_ceil(4) * 4;
            if step > data.len() {
                return;
            }

            data = &data[step..];
        }
    }
}

fn push_u16_attribute(out: &mut Vec<u8>, kind: u8, value: u16) {
    out.push(kind << 1);
    out.push(4);
    out.extend_from_slice(&value.to_be_bytes());
}

fn push_status_attribute(out: &mut Vec<u8>, status: RequestStatus) {
    out.push(ATTR_REQUEST_STATUS << 1);
    out.push(4);
    out.push(status as u8);
    out.push(0); // queue position: none, since this implementation grants or denies at once
}

/// Writes a grouped attribute whose value starts with the id it is about.
fn push_grouped(out: &mut Vec<u8>, kind: u8, contents: &[u8]) {
    // The grouped value is: two bytes of id, then the attributes; the id is the first attribute's.
    let id = if contents.len() >= 4 { [contents[2], contents[3]] } else { [0, 0] };
    let length = 2 + 2 + contents.len();
    out.push((kind << 1) | 1); // mandatory
    out.push(length.min(255) as u8);
    out.extend_from_slice(&id);
    out.extend_from_slice(contents);
    // Pad the group itself to a four-byte boundary.
    let padding = (4 - (length % 4)) % 4;
    out.extend(std::iter::repeat_n(0u8, padding));
}

/// What a floor control session wants done.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum FloorEvent {
    /// Send these bytes to the peer.
    Send(Vec<u8>),
    /// The floor named is ours to use.
    Granted(u16),
    /// The floor named was refused, or taken away again.
    Denied(u16),
    /// Somebody else was given the floor (server side: who now holds it).
    Held { floor: u16, user: u16 },
    /// The floor was let go.
    Released(u16),
}

/// The side of the conversation this endpoint is playing.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum FloorRole {
    /// Asks for floors: an ordinary participant.
    Participant,
    /// Hands them out: the conference itself.
    Server,
}

/// How long a request waits for an answer before it is repeated, and how many times.
const RETRY: Duration = Duration::from_millis(500);
const MAX_RETRIES: u8 = 6;

/// One floor control session over one transport.
pub struct FloorSession {
    role: FloorRole,
    conference: u32,
    user: u16,
    transaction: u16,
    request_id: u16,
    /// What is outstanding: the message, when it went, and how many times it has gone.
    pending: Option<(Vec<u8>, Instant, u8)>,
    /// Floors this side holds, or has asked for, and the request that did the asking.
    requests: HashMap<u16, u16>,
    /// Server side: who holds each floor.
    holders: HashMap<u16, u16>,
}

impl FloorSession {
    /// Starts a session. `user` is the id the conference gave this endpoint in the SDP.
    pub fn new(role: FloorRole, conference: u32, user: u16) -> Self {
        Self {
            role,
            conference,
            user,
            transaction: 1,
            request_id: 1,
            pending: None,
            requests: HashMap::new(),
            holders: HashMap::new(),
        }
    }

    /// Says hello, which is how a participant learns the server is there.
    pub fn start(&mut self, now: Instant, events: &mut Vec<FloorEvent>) {
        if self.role == FloorRole::Participant {
            let message = Message::new(Primitive::Hello, self.conference, self.next_transaction(), self.user);
            self.arm(message.encode(), now, events);
        }
    }

    /// Asks for a floor. The answer arrives as `Granted` or `Denied`.
    pub fn request(&mut self, floor: u16, now: Instant, events: &mut Vec<FloorEvent>) {
        if self.role != FloorRole::Participant {
            return;
        }

        let request = self.next_request();
        self.requests.insert(floor, request);
        let mut message = Message::new(Primitive::FloorRequest, self.conference, self.next_transaction(), self.user);
        message.floors.push(floor);
        self.arm(message.encode(), now, events);
    }

    /// Gives a floor back.
    pub fn release(&mut self, floor: u16, now: Instant, events: &mut Vec<FloorEvent>) {
        let Some(request) = self.requests.remove(&floor) else { return };
        let mut message = Message::new(Primitive::FloorRelease, self.conference, self.next_transaction(), self.user);
        message.request_id = Some(request);
        self.arm(message.encode(), now, events);
    }

    /// Repeats what has not been answered, and gives up after a while.
    pub fn poll_timeout(&mut self, now: Instant, events: &mut Vec<FloorEvent>) {
        let Some((message, sent, retries)) = self.pending.clone() else { return };
        if now.duration_since(sent) < RETRY {
            return;
        }

        if retries >= MAX_RETRIES {
            self.pending = None;
            for (floor, _) in self.requests.drain() {
                events.push(FloorEvent::Denied(floor));
            }

            return;
        }

        self.pending = Some((message.clone(), now, retries + 1));
        events.push(FloorEvent::Send(message));
    }

    /// Feeds in a message from the peer.
    pub fn handle(&mut self, data: &[u8], now: Instant, events: &mut Vec<FloorEvent>) {
        let Some(message) = Message::decode(data) else { return };
        if message.conference != self.conference {
            return;
        }

        match message.primitive {
            Primitive::Hello if self.role == FloorRole::Server => {
                let ack = Message::new(Primitive::HelloAck, self.conference, message.transaction, message.user);
                events.push(FloorEvent::Send(ack.encode()));
            }
            Primitive::HelloAck => self.pending = None,
            Primitive::FloorRequest if self.role == FloorRole::Server => {
                self.answer_request(&message, events);
            }
            Primitive::FloorRelease if self.role == FloorRole::Server => {
                self.answer_release(&message, events);
            }
            Primitive::FloorRequestStatus if self.role == FloorRole::Participant => {
                self.pending = None;
                let floor = message.floors.first().copied().or_else(|| self.requests.keys().next().copied());
                if let (Some(floor), Some(status)) = (floor, message.status) {
                    match status {
                        RequestStatus::Granted => events.push(FloorEvent::Granted(floor)),
                        RequestStatus::Denied | RequestStatus::Revoked | RequestStatus::Cancelled => {
                            self.requests.remove(&floor);
                            events.push(FloorEvent::Denied(floor));
                        }
                        RequestStatus::Released => {
                            self.requests.remove(&floor);
                            events.push(FloorEvent::Released(floor));
                        }
                        RequestStatus::Pending | RequestStatus::Accepted => {}
                    }
                }

                // Every status is acknowledged, or the server keeps sending it.
                let ack = Message::new(Primitive::FloorRequestStatusAck, self.conference, message.transaction, self.user);
                events.push(FloorEvent::Send(ack.encode()));
            }
            Primitive::Goodbye => {
                let ack = Message::new(Primitive::GoodbyeAck, self.conference, message.transaction, message.user);
                events.push(FloorEvent::Send(ack.encode()));
            }
            _ => {}
        }

        let _ = now;
    }

    /// Server side: grant a free floor, refuse one that is taken.
    fn answer_request(&mut self, message: &Message, events: &mut Vec<FloorEvent>) {
        let floor = message.floors.first().copied().unwrap_or(1);
        let request = self.next_request();
        let free = self.holders.get(&floor).is_none_or(|holder| *holder == message.user);
        let status = if free { RequestStatus::Granted } else { RequestStatus::Denied };
        if free {
            self.holders.insert(floor, message.user);
            events.push(FloorEvent::Held { floor, user: message.user });
        }

        let mut answer = Message::new(Primitive::FloorRequestStatus, self.conference, message.transaction, message.user);
        answer.floors.push(floor);
        answer.request_id = Some(request);
        answer.status = Some(status);
        events.push(FloorEvent::Send(answer.encode()));
    }

    /// Server side: whoever held the floor has let it go.
    fn answer_release(&mut self, message: &Message, events: &mut Vec<FloorEvent>) {
        let floor = self.holders.iter().find(|(_, holder)| **holder == message.user).map(|(floor, _)| *floor);
        let Some(floor) = floor else { return };
        self.holders.remove(&floor);
        events.push(FloorEvent::Released(floor));

        let mut answer = Message::new(Primitive::FloorRequestStatus, self.conference, message.transaction, message.user);
        answer.floors.push(floor);
        answer.request_id = message.request_id;
        answer.status = Some(RequestStatus::Released);
        events.push(FloorEvent::Send(answer.encode()));
    }

    /// Whether this side may send on a floor right now.
    pub fn holds(&self, floor: u16) -> bool {
        match self.role {
            FloorRole::Participant => self.requests.contains_key(&floor),
            FloorRole::Server => self.holders.get(&floor) == Some(&self.user),
        }
    }

    fn arm(&mut self, message: Vec<u8>, now: Instant, events: &mut Vec<FloorEvent>) {
        self.pending = Some((message.clone(), now, 0));
        events.push(FloorEvent::Send(message));
    }

    fn next_transaction(&mut self) -> u16 {
        self.transaction = self.transaction.wrapping_add(1).max(1);
        self.transaction
    }

    fn next_request(&mut self) -> u16 {
        self.request_id = self.request_id.wrapping_add(1).max(1);
        self.request_id
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn a_message_survives_the_round_trip() {
        let mut message = Message::new(Primitive::FloorRequest, 0xDEAD_BEEF, 42, 7);
        message.floors.push(2);
        let bytes = message.encode();

        // Version 2, the primitive, and a length counted in whole words.
        assert_eq!(bytes[0] >> 6, 2);
        assert_eq!(bytes[1], Primitive::FloorRequest as u8);
        assert_eq!(bytes.len() % 4, 0);

        let read = Message::decode(&bytes).expect("a message");
        assert_eq!(read.primitive, Primitive::FloorRequest);
        assert_eq!(read.conference, 0xDEAD_BEEF);
        assert_eq!(read.transaction, 42);
        assert_eq!(read.user, 7);
        assert_eq!(read.floors, vec![2]);
    }

    #[test]
    fn a_status_carries_its_grouped_attributes() {
        let mut message = Message::new(Primitive::FloorRequestStatus, 1, 9, 3);
        message.floors.push(5);
        message.request_id = Some(11);
        message.status = Some(RequestStatus::Granted);

        let read = Message::decode(&message.encode()).expect("a message");
        assert_eq!(read.request_id, Some(11), "the request id is found inside the grouping");
        assert_eq!(read.status, Some(RequestStatus::Granted));
        assert_eq!(read.floors, vec![5]);
    }

    #[test]
    fn rubbish_is_not_a_message() {
        assert!(Message::decode(&[]).is_none());
        assert!(Message::decode(&[0; 8]).is_none(), "too short for a header");
        assert!(Message::decode(&[0x80, 99, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]).is_none(), "not a primitive we know");
        // A length that runs past the end is refused rather than read.
        assert!(Message::decode(&[0x80, 1, 0, 9, 0, 0, 0, 1, 0, 1, 0, 1]).is_none());
    }

    #[test]
    fn a_floor_is_asked_for_granted_and_given_back() {
        let now = Instant::now();
        let mut participant = FloorSession::new(FloorRole::Participant, 77, 3);
        let mut server = FloorSession::new(FloorRole::Server, 77, 1);

        // Hello, and the server says hello back.
        let mut events = Vec::new();
        participant.start(now, &mut events);
        let hello = sent(&events).expect("a hello");
        let mut answers = Vec::new();
        server.handle(&hello, now, &mut answers);
        let ack = sent(&answers).expect("a hello ack");
        participant.handle(&ack, now, &mut Vec::new());

        // The screen's floor is asked for and granted.
        let mut events = Vec::new();
        participant.request(2, now, &mut events);
        let request = sent(&events).expect("a floor request");
        let mut answers = Vec::new();
        server.handle(&request, now, &mut answers);
        assert!(answers.iter().any(|e| matches!(e, FloorEvent::Held { floor: 2, user: 3 })), "{answers:?}");

        let status = sent(&answers).expect("a status");
        let mut granted = Vec::new();
        participant.handle(&status, now, &mut granted);
        assert!(granted.contains(&FloorEvent::Granted(2)), "{granted:?}");
        assert!(participant.holds(2));

        // Somebody else asking now is refused.
        let mut other = FloorSession::new(FloorRole::Participant, 77, 4);
        let mut events = Vec::new();
        other.request(2, now, &mut events);
        let mut answers = Vec::new();
        server.handle(&sent(&events).expect("a request"), now, &mut answers);
        let mut denied = Vec::new();
        other.handle(&sent(&answers).expect("a status"), now, &mut denied);
        assert!(denied.contains(&FloorEvent::Denied(2)), "{denied:?}");

        // And when the first one lets go, the floor is free again.
        let mut events = Vec::new();
        participant.release(2, now, &mut events);
        let mut answers = Vec::new();
        server.handle(&sent(&events).expect("a release"), now, &mut answers);
        assert!(answers.contains(&FloorEvent::Released(2)), "{answers:?}");
        assert!(!participant.holds(2));

        let mut events = Vec::new();
        other.request(2, now, &mut events);
        let mut answers = Vec::new();
        server.handle(&sent(&events).expect("a request"), now, &mut answers);
        assert!(answers.iter().any(|e| matches!(e, FloorEvent::Held { floor: 2, user: 4 })), "{answers:?}");
    }

    #[test]
    fn a_request_nobody_answers_is_repeated_and_then_given_up() {
        let mut now = Instant::now();
        let mut participant = FloorSession::new(FloorRole::Participant, 5, 2);
        let mut events = Vec::new();
        participant.request(1, now, &mut events);
        assert_eq!(events.len(), 1, "the first attempt goes out at once");

        let mut repeats = 0;
        let mut denied = false;
        for _ in 0..20 {
            now += Duration::from_millis(600);
            let mut events = Vec::new();
            participant.poll_timeout(now, &mut events);
            repeats += events.iter().filter(|e| matches!(e, FloorEvent::Send(_))).count();
            denied |= events.contains(&FloorEvent::Denied(1));
        }

        assert_eq!(repeats, MAX_RETRIES as usize, "it is repeated a fixed number of times");
        assert!(denied, "and then the caller is told it is not happening");
    }

    fn sent(events: &[FloorEvent]) -> Option<Vec<u8>> {
        events.iter().find_map(|e| match e {
            FloorEvent::Send(bytes) => Some(bytes.clone()),
            _ => None,
        })
    }
}

//! TLS for SIP (SIPS, RFC 3261 §26 / RFC 5630) on rustls with the ring provider.

use std::fs;
use std::sync::Arc;

use ring::digest;
use rustls::client::danger::{HandshakeSignatureValid, ServerCertVerified, ServerCertVerifier};
use rustls::client::WebPkiServerVerifier;
use rustls::crypto::{verify_tls12_signature, verify_tls13_signature, CryptoProvider};
use rustls::pki_types::pem::PemObject;
use rustls::pki_types::{CertificateDer, PrivateKeyDer, PrivatePkcs8KeyDer, ServerName, UnixTime};
use rustls::{ClientConfig, DigitallySignedStruct, RootCertStore, ServerConfig, SignatureScheme};

/// TLS options taken from the endpoint configuration.
#[derive(Debug, Clone, Default)]
pub struct TlsSettings {
    /// Validate the server certificate chain and name against the trust roots.
    pub verify_server: bool,
    /// Extra PEM trust anchors (a private PBX CA), added to the Mozilla roots.
    pub ca_file: Option<String>,
    /// SHA-256 fingerprints of accepted server certificates. When set, a matching certificate is
    /// accepted even if it is self-signed, and any other certificate is rejected.
    pub pinned_sha256: Vec<String>,
    /// PEM certificate chain and private key presented to peers. A self-signed certificate is
    /// generated when omitted.
    pub certificate_file: Option<String>,
    pub private_key_file: Option<String>,
}

/// Client and server configurations plus the fingerprint of the local certificate.
pub struct TlsContext {
    pub client: Arc<ClientConfig>,
    pub server: Arc<ServerConfig>,
    pub fingerprint: String,
}

pub fn provider() -> Arc<CryptoProvider> {
    Arc::new(rustls::crypto::ring::default_provider())
}

/// `AA:BB:…` SHA-256 fingerprint of a DER certificate (the form used by SDP `a=fingerprint`).
pub fn fingerprint_sha256(der: &[u8]) -> String {
    let d = digest::digest(&digest::SHA256, der);
    let mut s = String::with_capacity(95);
    for (i, b) in d.as_ref().iter().enumerate() {
        if i > 0 {
            s.push(':');
        }
        s.push_str(&format!("{b:02X}"));
    }
    s
}

fn normalize_fingerprint(s: &str) -> String {
    s.chars().filter(|c| c.is_ascii_hexdigit()).map(|c| c.to_ascii_uppercase()).collect()
}

/// A local certificate and its private key.
pub struct Identity {
    pub chain: Vec<CertificateDer<'static>>,
    pub key: PrivateKeyDer<'static>,
}

impl Identity {
    /// Generates a self-signed ECDSA P-256 certificate.
    pub fn self_signed(names: Vec<String>) -> Result<Self, String> {
        let ck = rcgen::generate_simple_self_signed(names).map_err(|e| e.to_string())?;
        Ok(Self {
            chain: vec![ck.cert.der().clone()],
            key: PrivateKeyDer::Pkcs8(PrivatePkcs8KeyDer::from(ck.signing_key.serialize_der())),
        })
    }

    pub fn load(cert_file: &str, key_file: &str) -> Result<Self, String> {
        let chain = CertificateDer::pem_file_iter(cert_file)
            .map_err(|e| format!("tlsCertificateFile: {e}"))?
            .collect::<Result<Vec<_>, _>>()
            .map_err(|e| format!("tlsCertificateFile: {e}"))?;
        if chain.is_empty() {
            return Err("tlsCertificateFile: no certificates found".into());
        }
        let key = PrivateKeyDer::from_pem_file(key_file).map_err(|e| format!("tlsPrivateKeyFile: {e}"))?;
        Ok(Self { chain, key })
    }

    pub fn fingerprint(&self) -> String {
        fingerprint_sha256(&self.chain[0])
    }
}

impl TlsContext {
    pub fn new(settings: &TlsSettings, local_names: Vec<String>) -> Result<Self, String> {
        let provider = provider();
        let identity = match (&settings.certificate_file, &settings.private_key_file) {
            (Some(c), Some(k)) => Identity::load(c, k)?,
            (None, None) => Identity::self_signed(local_names)?,
            _ => return Err("tlsCertificateFile and tlsPrivateKeyFile must be set together".into()),
        };
        let fingerprint = identity.fingerprint();

        let mut roots = RootCertStore { roots: webpki_roots::TLS_SERVER_ROOTS.to_vec() };
        if let Some(ca) = &settings.ca_file {
            let text = fs::read(ca).map_err(|e| format!("tlsCaFile: {e}"))?;
            for cert in CertificateDer::pem_slice_iter(&text) {
                let cert = cert.map_err(|e| format!("tlsCaFile: {e}"))?;
                roots.add(cert).map_err(|e| format!("tlsCaFile: {e}"))?;
            }
        }
        let webpki = WebPkiServerVerifier::builder_with_provider(Arc::new(roots), provider.clone())
            .build()
            .map_err(|e| e.to_string())?;
        let verifier = Arc::new(SipServerVerifier {
            webpki,
            provider: provider.clone(),
            verify: settings.verify_server,
            pins: settings.pinned_sha256.iter().map(|p| normalize_fingerprint(p)).filter(|p| !p.is_empty()).collect(),
        });

        let client = ClientConfig::builder_with_provider(provider.clone())
            .with_safe_default_protocol_versions()
            .map_err(|e| e.to_string())?
            .dangerous()
            .with_custom_certificate_verifier(verifier)
            .with_no_client_auth();

        let server = ServerConfig::builder_with_provider(provider)
            .with_safe_default_protocol_versions()
            .map_err(|e| e.to_string())?
            .with_no_client_auth()
            .with_single_cert(identity.chain, identity.key)
            .map_err(|e| format!("TLS certificate: {e}"))?;

        Ok(Self { client: Arc::new(client), server: Arc::new(server), fingerprint })
    }
}

#[derive(Debug)]
struct SipServerVerifier {
    webpki: Arc<WebPkiServerVerifier>,
    provider: Arc<CryptoProvider>,
    verify: bool,
    pins: Vec<String>,
}

impl ServerCertVerifier for SipServerVerifier {
    fn verify_server_cert(
        &self,
        end_entity: &CertificateDer<'_>,
        intermediates: &[CertificateDer<'_>],
        server_name: &ServerName<'_>,
        ocsp_response: &[u8],
        now: UnixTime,
    ) -> Result<ServerCertVerified, rustls::Error> {
        if !self.pins.is_empty() {
            let actual = normalize_fingerprint(&fingerprint_sha256(end_entity));
            return if self.pins.iter().any(|p| *p == actual) {
                Ok(ServerCertVerified::assertion())
            } else {
                Err(rustls::Error::General(format!("certificate fingerprint {actual} is not pinned")))
            };
        }
        if !self.verify {
            return Ok(ServerCertVerified::assertion());
        }
        self.webpki.verify_server_cert(end_entity, intermediates, server_name, ocsp_response, now)
    }

    // Handshake signatures are always checked, so a pinned or unverified certificate still proves
    // possession of its private key.
    fn verify_tls12_signature(
        &self,
        message: &[u8],
        cert: &CertificateDer<'_>,
        dss: &DigitallySignedStruct,
    ) -> Result<HandshakeSignatureValid, rustls::Error> {
        verify_tls12_signature(message, cert, dss, &self.provider.signature_verification_algorithms)
    }

    fn verify_tls13_signature(
        &self,
        message: &[u8],
        cert: &CertificateDer<'_>,
        dss: &DigitallySignedStruct,
    ) -> Result<HandshakeSignatureValid, rustls::Error> {
        verify_tls13_signature(message, cert, dss, &self.provider.signature_verification_algorithms)
    }

    fn supported_verify_schemes(&self) -> Vec<SignatureScheme> {
        self.provider.signature_verification_algorithms.supported_schemes()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn self_signed_context_and_fingerprint() {
        let ctx = TlsContext::new(&TlsSettings { verify_server: true, ..Default::default() }, vec!["localhost".into()]).unwrap();
        assert_eq!(ctx.fingerprint.len(), 95);
        assert_eq!(normalize_fingerprint(&ctx.fingerprint).len(), 64);
        assert_eq!(normalize_fingerprint("ab:cd"), "ABCD");
    }
}

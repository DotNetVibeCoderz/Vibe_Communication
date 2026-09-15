//! TLS configuration (rustls, ring provider) and certificate helpers.

use std::sync::Arc;

use parking_lot::Mutex;
use rustls::DigitallySignedStruct;
use rustls::client::WebPkiServerVerifier;
use rustls::client::danger::{HandshakeSignatureValid, ServerCertVerified, ServerCertVerifier};
use rustls::crypto::CryptoProvider;
use rustls_pki_types::pem::PemObject;
use rustls_pki_types::{CertificateDer, PrivateKeyDer, ServerName, UnixTime};
use sha1::Digest as _;

use crate::config::{ClientConfig, TlsVerification};
use crate::{ClientError, Result};

/// Hex encoded SHA-1 fingerprint of a DER certificate (Mumble's certificate hash format).
pub fn sha1_fingerprint(der: &[u8]) -> String {
    hex::encode(sha1::Sha1::digest(der))
}

/// Hex encoded SHA-256 fingerprint of a DER certificate.
pub fn sha256_fingerprint(der: &[u8]) -> String {
    hex::encode(ring::digest::digest(&ring::digest::SHA256, der))
}

/// A freshly generated self-signed identity.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct GeneratedCertificate {
    pub certificate_pem: String,
    pub private_key_pem: String,
    /// SHA-1 hash as seen by the server (identifies the registered user).
    pub sha1_fingerprint: String,
}

/// Generates a self-signed client certificate for `common_name`.
pub fn generate_certificate(common_name: &str) -> Result<GeneratedCertificate> {
    let mut params = rcgen::CertificateParams::new(Vec::<String>::new())
        .map_err(|e| ClientError::Certificate(e.to_string()))?;
    params.distinguished_name.push(rcgen::DnType::CommonName, common_name);
    params.not_after = rcgen::date_time_ymd(2100, 1, 1);
    let key = rcgen::KeyPair::generate().map_err(|e| ClientError::Certificate(e.to_string()))?;
    let cert = params.self_signed(&key).map_err(|e| ClientError::Certificate(e.to_string()))?;
    Ok(GeneratedCertificate {
        certificate_pem: cert.pem(),
        private_key_pem: key.serialize_pem(),
        sha1_fingerprint: sha1_fingerprint(cert.der()),
    })
}

/// Records the peer certificate and applies the configured verification policy.
#[derive(Debug)]
pub(crate) struct MumbleVerifier {
    mode: TlsVerification,
    pinned: Option<String>,
    provider: Arc<CryptoProvider>,
    webpki: Option<Arc<WebPkiServerVerifier>>,
    pub peer: Mutex<Option<Vec<u8>>>,
}

impl ServerCertVerifier for MumbleVerifier {
    fn verify_server_cert(
        &self,
        end_entity: &CertificateDer<'_>,
        intermediates: &[CertificateDer<'_>],
        server_name: &ServerName<'_>,
        ocsp_response: &[u8],
        now: UnixTime,
    ) -> std::result::Result<ServerCertVerified, rustls::Error> {
        *self.peer.lock() = Some(end_entity.to_vec());
        match self.mode {
            TlsVerification::AcceptAll => Ok(ServerCertVerified::assertion()),
            TlsVerification::Pinned => {
                let fp = sha1_fingerprint(end_entity);
                let expected = self.pinned.as_deref().unwrap_or_default().replace([':', ' '], "").to_lowercase();
                if fp == expected {
                    Ok(ServerCertVerified::assertion())
                } else {
                    Err(rustls::Error::General(format!("certificate fingerprint mismatch (got {fp})")))
                }
            }
            TlsVerification::WebPki => self
                .webpki
                .as_ref()
                .expect("webpki verifier")
                .verify_server_cert(end_entity, intermediates, server_name, ocsp_response, now),
        }
    }

    fn verify_tls12_signature(
        &self,
        message: &[u8],
        cert: &CertificateDer<'_>,
        dss: &DigitallySignedStruct,
    ) -> std::result::Result<HandshakeSignatureValid, rustls::Error> {
        rustls::crypto::verify_tls12_signature(message, cert, dss, &self.provider.signature_verification_algorithms)
    }

    fn verify_tls13_signature(
        &self,
        message: &[u8],
        cert: &CertificateDer<'_>,
        dss: &DigitallySignedStruct,
    ) -> std::result::Result<HandshakeSignatureValid, rustls::Error> {
        rustls::crypto::verify_tls13_signature(message, cert, dss, &self.provider.signature_verification_algorithms)
    }

    fn supported_verify_schemes(&self) -> Vec<rustls::SignatureScheme> {
        self.provider.signature_verification_algorithms.supported_schemes()
    }
}

/// Builds the rustls client configuration for a connection.
pub(crate) fn client_config(cfg: &ClientConfig) -> Result<(rustls::ClientConfig, Arc<MumbleVerifier>)> {
    let provider = Arc::new(rustls::crypto::ring::default_provider());
    let webpki = if cfg.tls_verification == TlsVerification::WebPki {
        let roots = rustls::RootCertStore::from_iter(webpki_roots::TLS_SERVER_ROOTS.iter().cloned());
        Some(
            WebPkiServerVerifier::builder_with_provider(Arc::new(roots), provider.clone())
                .build()
                .map_err(|e| ClientError::Tls(e.to_string()))?,
        )
    } else {
        None
    };
    let verifier = Arc::new(MumbleVerifier {
        mode: cfg.tls_verification,
        pinned: cfg.pinned_fingerprint.clone(),
        provider: provider.clone(),
        webpki,
        peer: Mutex::new(None),
    });

    let builder = rustls::ClientConfig::builder_with_provider(provider)
        .with_safe_default_protocol_versions()
        .map_err(|e| ClientError::Tls(e.to_string()))?
        .dangerous()
        .with_custom_certificate_verifier(verifier.clone());

    let config = match (&cfg.certificate_pem, &cfg.private_key_pem) {
        (Some(cert), Some(key)) => {
            let certs = CertificateDer::pem_slice_iter(cert.as_bytes())
                .collect::<std::result::Result<Vec<_>, _>>()
                .map_err(|e| ClientError::Certificate(e.to_string()))?;
            if certs.is_empty() {
                return Err(ClientError::Certificate("no certificate found in PEM".into()));
            }
            let key = PrivateKeyDer::from_pem_slice(key.as_bytes()).map_err(|e| ClientError::Certificate(e.to_string()))?;
            builder.with_client_auth_cert(certs, key).map_err(|e| ClientError::Certificate(e.to_string()))?
        }
        _ => builder.with_no_client_auth(),
    };
    Ok((config, verifier))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn generates_usable_identity() {
        let id = generate_certificate("RumbleBot").unwrap();
        assert!(id.certificate_pem.contains("BEGIN CERTIFICATE"));
        assert_eq!(id.sha1_fingerprint.len(), 40);
        let cfg = ClientConfig {
            certificate_pem: Some(id.certificate_pem),
            private_key_pem: Some(id.private_key_pem),
            ..Default::default()
        };
        client_config(&cfg).unwrap();
    }
}

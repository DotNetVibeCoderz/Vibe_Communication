//! Compiles the Mumble protobuf definitions with a pure-Rust protobuf compiler
//! (`protox`), so no system `protoc` installation is required.

fn main() -> Result<(), Box<dyn std::error::Error>> {
    println!("cargo:rerun-if-changed=proto/Mumble.proto");
    println!("cargo:rerun-if-changed=proto/MumbleUDP.proto");

    let descriptors = protox::compile(["Mumble.proto", "MumbleUDP.proto"], ["proto"])?;
    let mut config = prost_build::Config::new();
    config.bytes(["."]);
    config.compile_fds(descriptors)?;
    Ok(())
}

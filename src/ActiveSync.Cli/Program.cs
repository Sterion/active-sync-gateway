using ActiveSync.Cli;

// Slim `eas`: forward the command line to the running gateway's loopback /cli endpoint so everyday
// verbs run against warm services instead of paying a cold start of the full server app. `serve` and
// `protect` (the full app's pre-parse specials, which accept arbitrary --Section:Key=value overrides
// a strict parser would reject) always run locally; EAS_NO_FORWARD=1 forces everything local. When no
// gateway answers, fall back to running the full app locally so server-less/repair verbs still work.
//
// The request is SEALED with the ActiveSync:Encryption master key (read from the same config the
// server uses): possessing the key is the real auth — a co-located Kubernetes sidecar or host-network
// peer that shares loopback but NOT the key can't call /cli. Falls back to a plain body only when no
// key is configured (AllowPlaintext dev/test), where the server also relies on loopback alone. The
// RESPONSE is sealed the same way whenever a key exists — command output carries secrets too.
//
// (S8: the forwarding logic itself lives in EasForwardingClient, under the ActiveSync.Cli namespace,
// so it has a seam ActiveSync.Cli.Tests can construct and drive directly. This file stays the thin
// top-level entry point.)

return await EasForwardingClient.RunAsync(args);

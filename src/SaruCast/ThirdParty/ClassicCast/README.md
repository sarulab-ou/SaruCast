# Vendored Cast Streaming protocol components

Source: https://github.com/RikvdReijen/classic-chromecast-caster
Commit: `6cd9fdd1c7b1ca7d88f1023eff4f13a3032c5bab`
Copyright 2026 Rik van der Reijen (Rik Realization).
License: Apache-2.0; see `licenses/ClassicCast-LICENSE.txt` and `licenses/ClassicCast-NOTICE.txt` at the repository root.

Only RTP/RTCP, stream offer/answer models, H.264 Annex-B parsing, and the managed Opus adapter are included.
The upstream UI, capture, encoder process launcher, dependencies, analytics, and installer are not included.
SaruCast provides its own control channel, capture/encoder integration, lifecycle, and packaging.
Protocol fields and AES nonce construction were cross-checked against Open Screen and the independent CastMirror/chromiacast implementations.

Upstream protocol code is retained in its original namespace. Modified files identify SaruCast changes at the top of each file.

The Opus adapter explicitly disables optional native-library probing so packaged audio processing always uses the bundled managed implementation.

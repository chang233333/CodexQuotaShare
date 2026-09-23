# Third-party notices

Phase 1 (2026-09-18): QuotaScope tray source has been adapted; startup/lifecycle patterns informed the WinUI shell. No upstream application binary, branding asset, or real user fixture has been incorporated. The four upstream LICENSE files are reproduced verbatim under licenses/. Local snapshot evidence is in docs/reference-manifest.json; extracted archives do not establish an upstream commit.

| Project | URL | License / copyright | Reviewed components | Copied application code |
| --- | --- | --- | --- | --- |
| QuotaScope | https://github.com/EvanHexX/quota-scope | MIT; Copyright (c) 2026 HexX | WinUI packaging, Codex App Server, quota mapping, tray, autostart | app-winui/Tray/TrayIconHost.cs adapted to client/CodexQuotaShare.Windows/TrayIconHost.cs; namespace/class name and visibility diagnostics changed, MIT header retained |
| codex-meter | https://github.com/srmdn/codex-meter | MIT; Copyright (c) 2026 Said | Session parsing, synthetic quota/session data, initialization | None |
| Waveshare CodexMeter | https://github.com/waveshareteam/codex-meter | MIT; Copyright (c) 2026 CodexMeter contributors | HMAC, nonce protection, App Server handshake | None |
| Workers Chat Demo | https://github.com/cloudflare/workers-chat-demo | BSD-3-Clause; Copyright (c) 2020, Cloudflare. All rights reserved. | Durable Object routing, Hibernation, persistence, broadcast | None |

Full license texts: [QuotaScope](licenses/quota-LICENSE.txt), [codex-meter](licenses/meter-LICENSE.txt), [Waveshare](licenses/wave-LICENSE.txt), [Cloudflare](licenses/cloud-LICENSE.txt).

When copying or adapting source in later phases, update this document with the exact source/target files and changes, retain upstream notices, and include the applicable license texts in binary distributions. Review new packages and assets separately. BSD-3-Clause does not permit using upstream names to endorse this project.

This project is not affiliated with, endorsed by, or sponsored by OpenAI, Cloudflare, or the reference project maintainers. Codex is a product/service of OpenAI.

## Phase 1 implementation attribution

QuotaScope app-winui/Program.cs and App.xaml.cs informed the single-instance event, explicit dispatcher shutdown and WinRT startup sequence in the corresponding Windows client files. CodexCommandResolver follows its documented native install layout, with direct executable arguments and no shell fallback. Quota parsing and stdio transport are newly implemented for this project, informed by the reviewed protocols. Tests use newly authored synthetic values.

NuGet dependencies are pinned in client/CodexQuotaShare.Windows/CodexQuotaShare.Windows.csproj. Their transitive dependency licenses and notices are included under dependency-licenses/ in the portable package; Microsoft Windows SDK build tooling is a development dependency and not intentionally redistributed as an SDK.

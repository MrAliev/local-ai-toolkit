# Trust, privacy and signing

[Русская версия](Trust-and-security.ru.md) · [Home](Home.md)

| Layer | Current boundary |
| --- | --- |
| Local inference | The broker uses the configured backend; a caller may forward returned data |
| Package integrity | ECDSA-signed manifest and package hashes |
| Release-asset attestation | Records published bytes; does not by itself prove a build from the declared source |
| Windows publisher identity | SignPath onboarding remains planned in [#210](https://github.com/MrAliev/local-ai-toolkit/issues/210); no publisher certificate is confirmed here |

```mermaid
---
config:
  theme: base
  themeVariables:
    primaryColor: "#CCE4F7"
    primaryTextColor: "#1A1A1A"
    primaryBorderColor: "#2D7FD3"
    lineColor: "#0067C0"
    secondaryColor: "#FAFAFA"
    tertiaryColor: "#F3F3F3"
---
flowchart LR
  source["Reviewed source"] --> build["Trusted CI build"]
  build --> approval["Human signing approval"]
  approval --> own["Sign own PE binaries"]
  own --> package["Final ZIP"]
  package --> manifest["Hash and sign manifest"]
  manifest --> verify["Verify and publish"]
```

This is the **target signing route**, not proof that a signed release already exists.
Do not sign upstream DLLs as project-owned code. Do not modify a package after its hash
and manifest signature are computed.

[Code signing policy](../code-signing-policy.md) · [Privacy](../privacy.md) ·
[Publication checklist](../publication.md) · [Report a vulnerability privately](../../SECURITY.md).

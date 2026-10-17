# 13 — Document signatures (XML) and signing certificates

> **Status:** 2026-10-05. The user decided the tenant root CA is **P-256** (open question 6) and asked for **`YFex.Cryptography.Xml`**
> (open question 5). The package is built and tested (§2–§4). The certificate-source layer (§6) is a **proposal** awaiting the user.

## 1. Why a satellite package, and what is where

| Question | Answer |
|---|---|
| A separate repo "for certificate signatures"? | No. Loading, validating and holding keys is already this library's job (`CertificateFiles`, `CertificateTrust`, `CngSigningKeyStore`). A second repo would duplicate it. |
| Inside `YFex.Cryptography`? | No. Most apps never sign XML, and a non-fiscal checkout should not carry `System.Xml` signature code. |
| So | **`YFex.Cryptography.Xml`**: a satellite package in `C:\Dev\YFex\YFex.Cryptography`, BCL only. It references the library. |
| Microsoft's `System.Security.Cryptography.Xml`? | Not used (⚑K14). The net11 package still declares `RequiresUnreferencedCode`/`RequiresDynamicCode`: "CreateDigest is not trim compatible…", "XmlDsigXsltTransform … requires dynamic code". NFC-e is signed **on the checkout**, which is native AOT. The architecture test now bans `new SignedXml(` in the ecosystem. |
| Who owns the NF-e rules? | **Fiscal**: which element is signed, where `Signature` goes, which certificate signs which CNPJ, rejection codes. The package knows the XMLDSig profile only. |

## 2. The profile (MOC 7.0 §4.2.4, Tabela 4-2)

Enveloped XML-DSig, C14N 1.0 (`REC-xml-c14n-20010315`), `rsa-sha1` + `sha1` (protocol-mandated SHA-1, P-class ⚑K3), and the signer's
certificate alone in `X509Data` ("EndCertOnly"). eSocial and EFD-Reinf use the same shape with `rsa-sha256` + `sha256`
(`XmlSignatureAlgorithm.RsaSha256`). SEFAZ checks the ICP-Brasil chain and the revocation lists (LCR) itself; the signer must not
include them.

```csharp
XmlDocument nfe = XmlSignature.Load(xml);                          // DTD/XXE refused, whitespace preserved
var infNFe = (XmlElement)nfe.GetElementsByTagName("infNFe", NFeNamespace)[0]!;
XmlSignature.SignEnveloped(infNFe, certificate, privateKey);       // <NFe><infNFe Id="NFe…">…</infNFe><Signature…/></NFe>
string signed = nfe.OuterXml;                                      // never re-indent a signed document

XmlSignatureCheck check = XmlSignature.Verify(signature, [XmlSignatureAlgorithm.RsaSha1]);
if (check.Value is XmlSignatureVerified verified)
    Use(verified.SignedElement, verified.Signer);                  // read data from SignedElement, never by searching the Id again
// then validate verified.Signer: ICP-Brasil chain (CertificateTrust with the ICP-Brasil roots), validity at emission, CNPJ (§6)
```

`privateKey` is any `RSA`: A1 from PKCS#12, a Windows-store or A3-token key (`certificate.GetRSAPrivateKey()`), or a remote signer
(§5). The package never asks where the key lives.

## 3. Verification rules (strict by design)

- Exactly one profile is accepted: `Signature` = `SignedInfo`, `SignatureValue`, optional `KeyInfo` (no `Object`).
  - `SignedInfo` = C14N 1.0 without comments, `rsa-sha1` or `rsa-sha256`, **one** `Reference`.
  - Transforms ⊆ {enveloped-signature, C14N}.
  - The digest method must match the signature method.
  - Anything else is `UnsupportedProfile`.
- The caller may restrict algorithms (`AlgorithmNotAllowed`).
- `URI="#Id"` must name **exactly one** element carrying `Id`/`ID`/`id`. Two (a signature-wrapping attack) gives `ReferenceNotFound`.
  `URI=""` means the whole document; C14N then includes processing instructions outside the root (C14N §2.4).
- Outcomes: `XmlSignatureVerified(SignedElement, Signer, Algorithm)` or `XmlSignatureInvalid(Failure, Detail)`. This is a native
  union (cold path). It never throws for hostile input.
- **Certificate validity is separate and deliberate.** `Verify` proves only that this certificate's key signed. The ICP-Brasil chain,
  validity at the emission instant and the CNPJ match are §6 (MOC 4.2.4.1: CNPJ in OtherName OID `2.16.76.1.3.3`, CPF of an e-CPF in
  `2.16.76.1.3.1`).

## 4. Compatibility evidence (2026-10-05)

- **Oracle:** Microsoft's `SignedXml` (11.0.0-preview.5, from the local NuGet cache, run only in the throwaway project
  `C:\Temp\xmloracle`; the package is not referenced by any repo project). It signed 4 golden documents with a fixed RSA-2048 test key:
  - an NF-e (NFC-e, accents, `&amp;`);
  - an NF-e inside `enviNFe` with inherited `xmlns:xsi`, `xml:lang` and indentation;
  - a document with prefixes, namespaced attributes, `xmlns=""`, a PI, CDATA and comments;
  - an eSocial-style document signed whole (`URI=""`, RSA-SHA256, a top-level PI).
- RSA PKCS#1 v1.5 is deterministic: **DigestValue and SignatureValue are byte-identical** for all 4, and SignedXml's signatures verify
  here.
- **Deviation found in SignedXml (we follow the spec):** a tab in an attribute (`&#9;`) or a carriage return in text (`&#13;`).
  - C14N 1.0 §2.2 keeps them as `&#x9;`/`&#xD;`. SignedXml re-parses the element's `OuterXml` for `#Id` references, and XML
    normalization turns them into a space and a line feed, so its digest differs.
  - The NF-e schema's string pattern (`[!-ÿ]…`) excludes tab, CR and LF, so fiscal documents are not affected. Apache Santuario (Java)
    follows the spec.
- **.NET pitfall:** `ExportSubjectPublicKeyInfo()` on an `RSA` subclass that never set `KeySize` loops forever (it sizes its buffer
  from `KeySize = 0`). The package compares modulus + exponent instead. **A remote signer must set `KeySizeValue`.**
- Native AOT smoke: NF-e sign + verify + tamper detection, 0 IL warnings.

## 5. Where signing certificates can live: on the machine, or online?

Both are possible. What matters is **where the private key operation happens** and **whether it must work without internet**.

| Source | Where the key is | Works offline? | Notes |
|---|---|---|---|
| **A1 file** (`.pfx`, 1 year) | in our encrypted storage on the machine, loaded into memory when signing (`CertificateFiles.LoadPkcs12`, ephemeral key) | ✅ | Simplest. Keep it **out of the Windows store** (a PFX imported there is often exportable and usable by any app of that user). |
| **A1 kept at central, delivered to installations** | central vault (sealed with the tenant ring); each installation receives it sealed **to that installation**; re-sealed in device storage | ✅ after delivery | One upload, renewal pushed to every checkout of that CNPJ, revocable per installation. Needs a per-installation key-agreement key (§6). |
| **A3 token / smart card** (USB, 3–5 years) | the token's chip; Windows store entry + vendor middleware; `certificate.GetRSAPrivateKey()` returns a CNG/CSP key | ✅ while plugged in | PIN prompts come from the middleware. One token per machine. |
| **Cloud A3** ("certificado em nuvem": a PSC's HSM, e.g. BirdID, VIDaaS, NeoID, SafeID, RemoteID) | the provider's HSM; we send the 20/32-byte hash and receive the signature | ❌ needs internet + the holder's authorization (app/OTP/OAuth session) | Fits as an `RSA` subclass whose `SignHash` calls the provider (proven by a test with a fake remote). APIs differ per provider; one adapter package per provider when needed. |
| **Central signing service** | central (A1 in the vault, or an HSM) signs on request | ❌ | The checkout sends the document and gets it back signed. Fine for back-office NF-e (mod 55). |

**Constraint that decides it for pharmacies:**
- In NFC-e offline contingency (`tpEmis = 9`, Manual de Contingência Offline NFC-e), the checkout must **sign the NFC-e at the
  sale**, without internet, and print the QR code.
- So every checkout that sells offline needs a **local key**: an A1 delivered to the installation, or an A3 token.
- Cloud A3 and central signing only work online. They suit back-office NF-e, not the offline checkout.
- Transmission to SEFAZ is also **mutual TLS 1.2 with an ICP-Brasil certificate** (MOC Tabela 4-3). The transmitter's certificate may
  differ from the signer's ("certificado digital do responsável pela transmissão"). A cloud certificate that only signs hashes cannot
  do a TLS handshake, so transmission then needs a local or central certificate.

## 6. Who owns which certificate code (proposal, awaiting the user)

| Concern | Home | Status |
|---|---|---|
| Load PKCS#12 / DER / PEM with safe defaults; validate a chain against given roots at a given time | `YFex.Cryptography` (`CertificateFiles`, `CertificateTrust`) | ✅ built (Phase 3) |
| XML-DSig sign/verify | `YFex.Cryptography.Xml` | ✅ built |
| **Signing-certificate abstraction** (proposal), e.g. `ISigningCertificate { X509Certificate2 Certificate; RSA PrivateKey; KeyProtection; }` + `ISigningCertificateSource.GetAsync(purpose, ct)`, with sources: PKCS#12 bytes from sealed storage; Windows store by thumbprint (A1 installed or A3 token); remote (`RSA` adapter) | abstraction + PKCS#12 source in `YFex.Cryptography`; Windows store / token source in `.Windows`; each cloud provider in its own small package | **proposal** |
| Presenting the A1 as a TLS **client** certificate on outbound HTTP (SEFAZ transmission, §5) | **YFex.Web**: `ClientCertificateCredential` + `UseClientCertificates()` (one connection pool per certificate; loads through `CertificateFiles.LoadPkcs12` with `DefaultKeySet` on Windows). Fiscal's `YFex.NFe.Sefaz` uses it | ✅ built 2026-10-14 (time README §10 T3) |
| ICP-Brasil specifics: read CNPJ/CPF from OtherName (`2.16.76.1.3.3` / `2.16.76.1.3.1`), A1/A3 policy OIDs, the ICP-Brasil root set (AC Raiz v5/v10/v11…) shipped with the app and updated by bundle, revocation via LCR download | a small satellite `YFex.Cryptography.IcpBrasil` (Brazil-specific but not fiscal-specific: also eSocial, contracts, PDF signatures later) | **proposal** |
| Which certificate signs for which CNPJ/emitter, upload at central, expiry alerts (30/15/7 days), renewal, distribution to the checkouts of that CNPJ, audit of who used it | **Fiscal** (policy + UI), on Offline's delivery mechanism | Fiscal design |
| Delivering a secret (the A1) **to one installation** | **Offline** bundle sealed to the installation. Needs a per-installation **key-agreement key** (ECDH P-256 in CNG/TPM next to the signing keys). The library has `RecipientSealing` with server-side rings today, so a CNG-held `IRecipientKey` would be added to `.Windows` | crypto addition when Offline/Fiscal schedule it |
| "Installing" a certificate in the Windows store | avoided for A1. Done by the vendor middleware for A3 tokens. Only if a third-party tool needs it: `X509Store` with `UserKeySet` and a non-exportable key, behind the same source abstraction | — |

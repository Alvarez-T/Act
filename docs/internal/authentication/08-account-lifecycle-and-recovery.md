# 08 — Account lifecycle, verification and recovery

## 1. How accounts come to exist

| Path | Typical realm | Proof at creation | Status after |
|---|---|---|---|
| **Self-service sign-up** (`Registration(r => r.SelfService())`) | shared (shoppers) | the contact the realm requires (`RequireVerified(Email | Phone | PhoneOrEmail)`) via a challenge, or a trusted external e-mail | `Active` once verified, else `PendingVerification` (can finish verification, cannot do anything else) |
| **Invitation** (`authentication.accounts.invite`) | per tenant (staff) | the invited contact receives a link/code (`ChallengePurpose.Invitation`, 7 days); accepting sets a password or links a provider | `Active` |
| **Admin-created** (`authentication.accounts.manage`) | per tenant | none — a temporary password (`PasswordChangeRequired(AdminReset)`) or operator code + PIN set at first use | `Active` |
| **Just-in-time from a required provider** (FarmaViva Entra, 06 §5) | per tenant | the provider (`ProviderEnforcement.Required` + allowed domain) | `Active` |
| **Bulk import** (migration from another system) | any | existing password hashes are **not** imported unless they are PBKDF2 in our format; otherwise accounts start with `PasswordChangeRequired(PolicyChanged)` + invitation | `Active` |

```csharp
public sealed record Registered(AccountId Account, SignedIn? SignedIn);        // SignedIn null when verification is still pending
public sealed record VerificationPending(SignInFlowToken Flow, MaskedDestination CodeSentTo);
public sealed record RegistrationRefused(RegistrationRefusal Reason);           // NotAllowed, PasswordRejected(PasswordPolicyResult), HumanVerificationRequired
public union RegistrationResult(Registered, VerificationPending, RegistrationRefused);
```

**Enumeration resistance at sign-up:** "this e-mail already has an account" is never shown. The flow sends a challenge either
way; when the address is taken, the message says "someone tried to create an account with your address — if it was you, sign in or
reset your password" and the registration silently ends.

## 2. Contacts and verification

- An account has 0..n **verified contacts** (`EmailAddress` / `PhoneNumber`), each with `VerifiedAt`, `UsableForSignIn`,
  `UsableForRecovery`, `PreferredChannel` (WhatsApp vs SMS for phones) and `IsPrimary` per kind.
- Unverified contacts are not stored on the account; they exist only inside a pending challenge (so an attacker cannot "park" a
  victim's address on their account).
- A contact is unique **per realm among verified contacts**. Verifying an address already verified on another account fails
  generically and notifies the other account.
- **Changing** the primary e-mail/phone: requires a recent step-up (5 min) **and** verifying the new contact; the old contact is
  notified ("your e-mail was changed to c***@…; not you? → recovery") and remains usable for recovery for **7 days**. Prevents a
  session thief from silently taking the account.

## 3. Password reset

```
start(identifier)                         always responds "if an account exists, we sent instructions" (same timing, 11 §1)
  ├─ account found, has usable recovery contacts → ResetPassword challenge to the preferred one (code; e-mail also gets a link)
  └─ not found / no contact → nothing sent (a dummy challenge id is returned, indistinguishable)
complete(challenge, code|link, newPassword)
  ├─ challenge Passed + account has a second factor → SecondFactorRequired (authenticator / passkey / recovery code)
  ├─ policy check on the new password (04 §1)
  └─ PasswordReset: new verifier, SecurityStamp++, ALL sessions + refresh families revoked, pending challenges cancelled,
     notification to every verified contact, AuthenticationEvents.PasswordReset
```

- Reset challenges: 15 min (e-mail link), 10 min (codes), single use, bound to the security stamp.
- A reset **does not** remove second factors and **does not** sign the user in by itself in staff realms (option
  `SignInAfterReset`, default on for shoppers, off for staff).
- Operator PIN reset at a store: the manager (permission `authentication.accounts.manage` in that scope) sets a temporary PIN for
  Ana from PC-GERENTE; Ana must change it at first use. Audited with both actors.

## 4. Lost second factor (account recovery)

| Situation | Path |
|---|---|
| has recovery codes | sign in with password + a recovery code (single use) → prompt to set up a new factor and regenerate codes |
| has another factor (passkey + authenticator) | use the other one |
| nothing left, shopper realm | recovery through **two** verified contacts (e-mail code **and** phone code) + a 24 h **cooling period** during which the old factor still works and every contact is notified; then factors are removed |
| nothing left, staff realm | an administrator resets the factors (§6) — no self-service, because staff accounts reach business data |

## 5. Disable, lock, delete

| Status | Who | Effect |
|---|---|---|
| `Locked` | automatic (throttling, 11 §2) or admin | sign-in refused until the lock ends or an admin unlocks; sessions keep running unless the admin also revokes |
| `Disabled` | admin (`authentication.accounts.manage`), or the app (an employee dismissed) | sessions revoked immediately (`RevokedAsync`), sign-in refused, external logins cannot sign in, offline verifiers are dropped from the next site bundle (Offline) |
| `Deleted` | the person (shoppers, after step-up) or admin; LGPD erasure request | personal data erased (contacts, display name, external keys' sealed parts, verifiers); the `AccountId` row stays as a **tombstone** so audit/logs that name the subject remain consistent; the e-mail can register again as a **new** account |

The library never decides **when** an employee is dismissed or how long data is retained — the app calls `DisableAsync`; retention of
session history follows the YFex.Privacy/Audit retention classes (11 §5).

## 6. Administration (⚑AN25)

```csharp
[PermissionCatalog]
public static partial class AuthenticationPermissions
{
    [Permission("authentication.accounts.view", Name = "Ver contas de acesso")]        public static partial Permission AccountsView { get; }
    [Permission("authentication.accounts.manage", Name = "Gerenciar contas de acesso")] public static partial Permission AccountsManage { get; }
    [Permission("authentication.accounts.invite", Name = "Convidar pessoas")]           public static partial Permission AccountsInvite { get; }
    [Permission("authentication.factors.reset", Name = "Redefinir segundo fator")]      public static partial Permission FactorsReset { get; }
    [Permission("authentication.sessions.revoke", Name = "Encerrar sessões")]           public static partial Permission SessionsRevoke { get; }
    [Permission("authentication.impersonate-user", Name = "Acessar como outra pessoa")] public static partial Permission ImpersonateUser { get; }
    [Permission("authentication.providers.configure", Name = "Configurar provedores")] public static partial Permission ProvidersConfigure { get; }
    [Permission("authentication.policy.configure", Name = "Configurar políticas de acesso")] public static partial Permission PolicyConfigure { get; }
}
```

- Admin operations are **CQRS commands** (`Authentication.Commands.DisableAccount`, `ResetFactors`, `RevokeSessions`, …) gated by
  the catalog, scoped by Authorization (a store manager manages only accounts whose home scope is beneath his store).
- `ResetFactors` and `ImpersonateUser` default to `.RequireApproval(...)` (Authorization approvals, separation of duties): one admin
  cannot silently take over another person's account.
- Every admin command emits an `AuthenticationEvents` entry with the real actor; Audit keeps them.

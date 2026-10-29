# FREE-FOREVER POLICY — Nova Download Manager

> **User directive (2026-10-29, original Persian):**
> «برنامه نباید برای هیچ قسمتیش نیاز ب پرداخت هزینه باشه»
>
> *"The program must not require payment for any part of it."*

This is a **hard project invariant**, equal in force to the security invariants in
PROJECT-RULES.md. It cannot be weakened silently (UNIVERSAL-RULES 3); changing it
requires an explicit user directive recorded in DECISIONS.md.

---

## The guarantee — تضمین

**Every feature of Nova is free. No part of the program — present or future —
requires payment.**

1. **No paid tiers.** There is no "Pro", "Premium", "Paid", "Licensed" or
   "Registered" mode. Every feature ships to every user, in every build, on every
   channel. No feature flags gate functionality behind payment.
2. **No license keys, no activation.** No serial numbers, no activation servers,
   no nag screens, no trial timers, no "days remaining" counters, no
   pay-to-remove-anything.
3. **No paid dependencies.** Every direct dependency must carry an
   OSI-approved free license (MIT, Apache-2.0, BSD, ISC, MPL-2.0, LGPL, GPL,
   Unlicense, CC0 / public domain). Verified continuously by
   `scripts/free_audit.py` and enforced in CI. See THIRD-PARTY-NOTICES.md.
4. **No paid build/signing services.** Code signing uses self-signed
   certificates only (free). **Azure Trusted Signing (~US$9.99/month) was
   researched and then REJECTED** by this policy (DECISIONS D-013). Binary
   integrity is guaranteed by the published SHA-256 sidecar of every artifact
   instead. See docs/VERSIONING.md §8.
5. **No paid distribution.** Releases go out via GitHub Releases / direct
   download. Optional browser-store publishing is a developer convenience
   (Chrome Web Store charges a one-time $5 developer registration to the
   *publisher*, never to users) — and is NOT required to install or use Nova.
6. **No paid third-party services integrated.** Nova never requires a
   subscription account (no premium hoster/decrypter plugins — explicitly
   excluded in docs/FEATURE-GAP-2026.md G19), no paid proxies, no paid VPN.
   If a user's own accounts/cookies are used to fetch login-gated files, that
   is the user's own relationship with that site — Nova itself charges nothing
   and adds nothing on top.
7. **No ads, no telemetry-for-money, no data brokerage.** No ad SDKs, no
   bundled offers, no monetization of user data. (Privacy posture unchanged
   — see docs/SECURITY.md.)
8. **No paywalled updates.** Every update — including major versions — is free
   forever. The update check points at a free host (GitHub Release asset or
   raw gist URL).

## Enforcement — اجرا و کنترل

| Layer | Mechanism |
|---|---|
| Dependency gate | `python3 scripts/free_audit.py` — every NuGet / pub / npm dependency must be listed (with license) in THIRD-PARTY-NOTICES.md; unlisted or paid-license dependency ⇒ exit 1 |
| Source gate | Same script scans tracked source + active docs for paid-service references (Azure Trusted Signing, Real-Debrid, Premiumize, AllDebrid, paid-license keywords, …); hits outside historical/policy context ⇒ exit 1 |
| CI gate | `.github/workflows/version-audit.yml` runs the free audit on every push/PR — a tree that violates the policy cannot build |
| Review gate | PRs adding commercial/paid dependencies or payment-requiring code are rejected by policy |
| Record | DECISIONS.md D-013 (Trusted Signing rejected); VERSIONS.md notes the policy round |

## Verification — راستی‌آزمایی

```bash
python3 scripts/free_audit.py          # dependency + term audit (CI runs this too)
python3 scripts/sync_versions.py --check
```

---

## خلاصهٔ فارسی — سیاست «همیشه رایگان»

**تمام امکانات نوا رایگان است. هیچ بخشی از برنامه — حالا یا در آینده — به پرداخت
هزینه نیاز ندارد.**

- هیچ نسخهٔ «حرفه‌ای» یا «پولی» وجود ندارد؛ همهٔ قابلیت‌ها برای همه کاربران و در همهٔ
  نسخه‌ها فعال است.
- بدون لایسنس‌کی، بدون فعال‌سازی، بدون محدودیت زمانی، بدون پیام‌های آزاردهنده، بدون
  نمایش تبلیغ.
- تمام وابستگی‌ها مجوز رایگان و متن‌باز دارند (MIT / Apache / BSD / …) و به‌صورت
  خودکار در CI ممیزی می‌شوند.
- خدمات امضای پولی (مانند Azure Trusted Signing با حدود ۱۰ دلار در ماه) بررسی و
  **رد شدند**. امضای برنامه در صورت نیاز با گواهی self-signed (رایگان) انجام می‌شود و
  صحت بسته‌ها با انتشار SHA-256 تضمین می‌شود.
- توزیع از طریق GitHub Releases انجام می‌شود؛ انتشار اختیاری در فروشگاه مرورگرها
  هرگز برای نصب یا استفاده لازم نیست.
- هیچ سرویس اشتراکی یا حساب پولی در برنامه ادغام نشده است (پلاگین‌های هاست‌های
  پولی صریحاً خارج از دامنه‌اند) و به‌روزرسانی‌ها همیشه رایگان‌اند.
- این سیاست یک قاعدهٔ ثابت پروژه است؛ تغییر آن فقط با دستور صریح شما و ثبت در
  DECISIONS.md ممکن است.

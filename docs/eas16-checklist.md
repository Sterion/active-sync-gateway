# EAS 16.0 / 16.1 uplift — spec audit & implementation checklist

Source of truth: **MS-ASWBXML v24.0 (2025-05-20)**, **MS-ASHTTP v23.0**, **MS-ASCMD
v2025-05-20** — token tables and command codes below were transcribed from the published
DOCX files, not from memory. This document tracks what the 16.x uplift covers; the
Status column is updated as commits land.

## WBXML token additions (complete 16.x diff, all code pages)

**Code page 4 — Calendar**

| Tag | Token | Versions |
|---|---|---|
| ClientUid | 0x3C | 16.0, 16.1 |

**Code page 8 — MeetingResponse**

| Tag | Token | Versions |
|---|---|---|
| ProposedStartTime | 0x10 | 16.1 |
| ProposedEndTime | 0x11 | 16.1 |
| SendResponse | 0x12 | 16.0, 16.1 |

**Code page 14 — Provision**

| Tag | Token | Versions |
|---|---|---|
| AccountOnlyRemoteWipe | 0x3B | 16.1 |

**Code page 17 — AirSyncBase**

| Tag | Token | Versions |
|---|---|---|
| Add | 0x1C | 16.0, 16.1 |
| Delete | 0x1D | 16.0, 16.1 |
| ClientId | 0x1E | 16.0, 16.1 |
| Content | 0x1F | 16.0, 16.1 |
| Location | 0x20 | 16.0, 16.1 |
| Annotation | 0x21 | 16.0, 16.1 |
| Street | 0x22 | 16.0, 16.1 |
| City | 0x23 | 16.0, 16.1 |
| State | 0x24 | 16.0, 16.1 |
| Country | 0x25 | 16.0, 16.1 |
| PostalCode | 0x26 | 16.0, 16.1 |
| Latitude | 0x27 | 16.0, 16.1 |
| Longitude | 0x28 | 16.0, 16.1 |
| Accuracy | 0x29 | 16.0, 16.1 |
| Altitude | 0x2A | 16.0, 16.1 |
| AltitudeAccuracy | 0x2B | 16.0, 16.1 |
| LocationUri | 0x2C | 16.0, 16.1 |
| InstanceId | 0x2D | 16.0, 16.1 |

**Code page 21 — ComposeMail**

| Tag | Token | Versions |
|---|---|---|
| Forwardees | 0x15 | 16.0, 16.1 |
| Forwardee | 0x16 | 16.0, 16.1 |
| Name | 0x17 | 16.0, 16.1 |
| Email | 0x18 | 16.0, 16.1 |

**Code page 22 — Email2**

| Tag | Token | Versions |
|---|---|---|
| IsDraft | 0x15 | 16.0, 16.1 |
| Bcc | 0x16 | 16.0, 16.1 |
| Send | 0x17 | 16.0, 16.1 |

**Code page 25 — Find** (new page, 16.1 only; note the 0x0F/0x10 and 0x1A–0x1F gaps)

| Tag | Token | Versions |
|---|---|---|
| Find | 0x05 | 16.1 |
| SearchId | 0x06 | 16.1 |
| ExecuteSearch | 0x07 | 16.1 |
| MailBoxSearchCriterion | 0x08 | 16.1 |
| Query | 0x09 | 16.1 |
| Status | 0x0A | 16.1 |
| FreeText | 0x0B | 16.1 |
| Options | 0x0C | 16.1 |
| Range | 0x0D | 16.1 |
| DeepTraversal | 0x0E | 16.1 |
| Response | 0x11 | 16.1 |
| Result | 0x12 | 16.1 |
| Properties | 0x13 | 16.1 |
| Preview | 0x14 | 16.1 |
| HasAttachments | 0x15 | 16.1 |
| Total | 0x16 | 16.1 |
| DisplayCc | 0x17 | 16.1 |
| DisplayBcc | 0x18 | 16.1 |
| GalSearchCriterion | 0x19 | 16.1 |
| MaxPictures | 0x20 | 16.1 |
| MaxSize | 0x21 | 16.1 |
| Picture | 0x22 | 16.1 |

## MS-ASHTTP changes

- Base64-query **command code 23 = Find** (16.1). Codes 5–8 (GetHierarchy,
  Create/Delete/MoveCollection) are retired and absent from the current table — our
  decoder keeps the placeholders for index alignment but the gateway never advertised
  those commands.
- Version bytes: 160 → "16.0", 161 → "16.1" (existing `versionByte/10 . versionByte%10`
  parse handles both).

## Behavior deltas to implement (from MS-ASCMD / MS-ASAIRS / MS-ASCAL / MS-ASEMAIL)

| # | Area | Delta | Status |
|---|------|-------|--------|
| 1 | Version negotiation | Advertise `12.1,14.0,14.1,16.0,16.1` (drop the dishonest 2.5/12.0 — GetHierarchy et al. were never implemented); per-request `EasVersion` gates. 14.1 responses stay byte-identical. | done |
| 2 | Calendar ClientUid | 16.x clients send calendar:ClientUid instead of a UID on Add; server generates the real UID and must NOT echo ClientUid back. | done |
| 3 | Calendar partial Change | 16.x Change carries only modified properties — merge, never replace (extends the existing presence-guard merge). | done |
| 4 | Occurrence operations | airsyncbase:InstanceId (0x2D) identifies one occurrence in Sync Delete/Change of recurring events. | done |
| 5 | Structured Location | 16.x moves the event location to airsyncbase:Location (DisplayName + address/geo children). ≤14.1 keeps calendar:Location. Map DisplayName ⇄ the iCal LOCATION property; other children unmapped for now. | done |
| 6 | Calendar attachments | airsyncbase:Attachments with Add/Delete/ClientId/Content on events; gated by `Dav:CalendarAttachments` (Auto/On/Off, per-user via overrides). | done |
| 7 | Drafts sync | Client Sync Add/Change of Email in the Drafts folder (To/Cc/email2:Bcc/Subject/Body, email2:IsDraft flag on server→client items); email2:Send inside Add/Change submits the draft. | done |
| 8 | SmartForward Forwardees | composemail:Forwardees(Forwardee(Name,Email)) — 16.x SmartForward without a MIME body. | done |
| 9 | MeetingResponse 16.x | SendResponse (16.0: whether/with what body to send the iTIP reply); ProposedStartTime/EndTime (16.1: counter-proposal). | tolerated — the elements decode and are accepted, but the iTIP reply is ALWAYS sent (pre-16 behavior) and proposals are not relayed |
| 10 | Find command | 16.1 replacement for mailbox/GAL Search: ExecuteSearch > MailBoxSearchCriterion(Query(FreeText, airsync:CollectionId?), Options(Range, DeepTraversal)) or GALSearchCriterion(Query(FreeText), Options(Range, Picture)); response Response > Collections?/Result(Class, ServerId/CollectionId, Properties(...)/GAL props incl. Picture), Total, Range. SearchId is client-generated and echoed. | done |
| 11 | AccountOnlyRemoteWipe | 16.1 Provision: pending wipe → provision:AccountOnlyRemoteWipe block; device acks with Status 1 in a follow-up Provision; partnership auto-blocked after the ack. NO full-wipe path exists. | done |
| 12 | SendMail/SmartSend from draft | ComposeMail referencing a saved draft item id (16.x clients may compose via Drafts instead). | done |

## Deliberately NOT implemented (documented omissions within 16.x)

- airsyncbase:Annotation, the Location address/geo sub-fields (Street…LocationUri —
  only DisplayName maps onto IMAP/CalDAV data), rm:* (Rights Management), and
  MeetingResponse counter-proposal forwarding (acknowledged, not relayed).
- Search remains available to all versions (Find does not replace it server-side).

## Reference extraction commands

The DOCX sources live at
`https://officeprotocoldocs-f5hpbjgea6b8gneq.b02.azurefd.net/files/MS-AS{WBXML,HTTP,CMD}/…`;
unzip `word/document.xml`, strip tags, and diff the "Protocol versions" columns for
`16.0`/`16.1`-only rows (that is how the tables above were produced).

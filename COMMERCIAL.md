# Commercial licensing

The ActiveSync Gateway is distributed under the [PolyForm Noncommercial License
1.0.0](LICENSE). That licence permits **any noncommercial purpose** — personal use,
hobby projects, study and research, and use by charities, educational institutions,
public research bodies, public safety and health organisations, environmental
organisations and government institutions.

It does **not** permit commercial use.

## What counts as commercial

Anything not covered by the permitted purposes in the licence — most commonly:

- running the gateway to provide mail sync for a business's own staff,
- offering it as a hosted or managed service to customers,
- bundling it into a product or appliance you sell,
- using it to deliver a paid service to third parties.

If you are unsure which side of the line you are on, ask — see below.

## Getting a commercial licence

Commercial licensing is **not currently offered**. This file exists so the
position is unambiguous rather than implied: if you need commercial terms,
open an issue or contact the maintainer and the request will be considered
on its own merits.

Please do not deploy commercially on the assumption that a licence will be
granted retroactively.

## The plugin contract is not restricted

If you are writing a **backend plugin**, you do not need any of the above. The
two published packages — `ActiveSync.Contracts` and `ActiveSync.Protocol` — are
[MIT licensed](LICENSE-MIT) precisely so that plugins, including commercial and
closed-source ones, can be built and distributed freely. See
[docs/plugins.md](docs/plugins.md).

## A note on Exchange ActiveSync

Exchange ActiveSync is a Microsoft protocol. This project is an independent
implementation written from Microsoft's published Open Specifications
(MS-ASHTTP, MS-ASWBXML, MS-ASCMD and the rest), which grant copyright permission
to implement them but expressly grant **no patent licence**.

Microsoft operates a separate [Exchange ActiveSync licensing
programme](https://www.microsoft.com/en-us/legal/intellectualproperty/tech-licensing/programs)
covering "devices, software, or servers". Anyone intending to commercialise an
EAS implementation — including a licensee of this software — is responsible for
determining their own position under that programme.

No claim is made here that this project holds, or is covered by, any Microsoft
patent licence. "Exchange ActiveSync", "Exchange" and "Microsoft" are trademarks
of Microsoft Corporation; they are used in this project only descriptively, to
identify the protocol implemented. This project is not affiliated with,
endorsed by, or certified by Microsoft.

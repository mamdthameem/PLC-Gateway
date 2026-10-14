# Installation code (cloud → gateway installer)

When a client is registered in the cloud, the cloud shows one **installation code** (Clients → Edit →
**Copy installation code**, available once the client is saved). It's pasted into
`ShotSense-Gateway-Setup.exe` on the client's PC — the wizard has a large box for it; click in it and
press Ctrl+V — and it is the only way the cloud's keys reach that PC: nobody copies keys into settings files
by hand.

The installer reads it in `installer/scripts/Common.ps1` (`ConvertFrom-InstallCode`). Keep that
function and this document in step.

## Format

```
SSG1.<base64url( UTF-8 JSON )>
```

- `SSG1.` is the prefix: "ShotSense Gateway, code format 1".
- Then the JSON below, UTF-8 encoded, as **base64url** (`-` and `_` instead of `+` and `/`). Padding
  `=` is optional.
- Whitespace and line breaks are ignored, so a code that wraps when it's pasted still works.

```json
{
  "v": 1,
  "clientId": "00837eb94ad026ed",
  "clientName": "Sree Sakthi Foundry",
  "publicHostname": "sreesakthi.shotsense.com",
  "licenseCheckUrl": "https://asia-south1-shotsense-13b1f.cloudfunctions.net/licenseCheck?clientId=00837eb94ad026ed",
  "licenseKey": "<64 hex characters>",
  "adminApiKey": "<64 hex characters>",
  "tunnelToken": "<Cloudflare tunnel token>",
  "siteProfileUrl": "https://asia-south1-shotsense-13b1f.cloudfunctions.net/siteProfile?clientId=00837eb94ad026ed"
}
```

| Field | Required | Rules | Becomes, on the gateway |
| --- | --- | --- | --- |
| `v` | yes | `1` | — (a different number is refused with "needs a newer installer") |
| `clientId` | yes | not empty | `ShotSense:ClientId` (shown in logs) and `Firebase:ClientId` — email sign-in accepts only this client's people |
| `clientName` | yes | not empty | `ShotSense:ClientName`, shown in the wizard so the installer can confirm the right client |
| `publicHostname` | when `tunnelToken` is set | host name only, no `https://` | `ShotSense:PublicHostname`; shown on the wizard's last page |
| `licenseCheckUrl` | yes | starts with `https://` | `License:CheckUrl` |
| `licenseKey` | yes | at least 32 characters | `License:Key`, sent by the gateway as `X-License-Key` |
| `adminApiKey` | yes | at least 32 characters | `Admin:ApiKey`; the cloud sends it as `X-Api-Key` on every `/api/admin/*` call |
| `tunnelToken` | no | the token Cloudflare gives for the client's tunnel | installs the `Cloudflared` Windows service. Leave it empty for a plant-network-only install |
| `siteProfileUrl` | no (added 2026-10-13) | starts with `https://` | `SiteProfile:Url` — where the gateway fetches the client's **tag file** at every start. A code without it gets the licence address with `siteProfile` in place of `licenseCheck` |

**The tag file must be uploaded in the cloud before installing** (Clients → Edit → Tag Settings File).
Setup checks for it and stops with that message if it is missing: the installer ships no tags, and a
gateway without its tag file cannot read the PLC. See `PLCGateway/Hosting/SiteProfile.cs`.

The installer writes these into `C:\ProgramData\ShotSense\appsettings.Production.json`, which only
administrators and the gateway service can read. It also generates the parts the cloud never sees:
the database passwords and the dashboard's JWT signing key.

## Generating it (cloud side)

The cloud builds it in `web/src/utils/installCode.ts` (`buildInstallCode`) from the saved client:
its id, name, address, keys and tunnel token. It is offered only while the dialog shows exactly what
is saved, so a copied code always holds keys the cloud accepts. The encoding, for reference:

```js
const bytes = new TextEncoder().encode(JSON.stringify(payload));
const b64 = btoa(String.fromCharCode(...bytes));
const code = 'SSG1.' + b64.replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
```

`tools/New-InstallCode.ps1` still builds one by hand from the same values, for testing.

## The tunnel token

The installer needs a **remotely managed** tunnel: one created in the Cloudflare dashboard, not with
`cloudflared tunnel create` in a terminal. A tunnel created in the terminal keeps its routing in a
file on that PC, so a token for it would connect but serve nothing.

For each client:

1. Cloudflare dashboard → **Zero Trust** → **Networks** → **Tunnels** → **Create a tunnel** →
   **Cloudflared**. Name it after the client.
2. On the install step, copy the token: it's the long value after `cloudflared.exe service install`.
   Ignore the command itself; the installer runs it.
3. **Public hostname**: subdomain `clientname`, your domain, service type **HTTP** (not HTTPS),
   URL `localhost:5200`. The gateway serves plain HTTP on that port; Cloudflare adds the HTTPS for
   visitors. Choosing HTTPS here gives every visitor **502 Bad Gateway** while the tunnel itself
   shows healthy.
4. Paste the token into the client's **Cloudflare Tunnel Token** field in the cloud and save. The
   cloud stores it with the client's keys (admins only) and puts it into the code.

Later the cloud can do all of this through Cloudflare's API at registration (create the tunnel, set
its public hostname, read the token), so registering a client produces a working subdomain with no
manual steps.

## Handling it

- **It contains every secret for that client.** Show it only to staff doing installs. Don't email it
  or save it in a file; paste it straight from the cloud into the installer.
- If a code leaks, generate new keys in the cloud and install again. An install over an uninstalled
  one ("Restore") takes the new keys and keeps all data.
- Changing keys on a running install currently means editing the settings file above and restarting
  the `ShotSenseGateway` service. The wizard doesn't offer a "change keys" option yet.

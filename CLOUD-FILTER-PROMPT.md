The gateway's admin API changed on 2026-10-12, and one change breaks something this app relies on. **`GET /api/admin/live` no longer contains `section2`.** Update the client page to match, as described below. When you're done, report what you changed.

## Why it changed

`section2` in `/live` was "the latest completed filter, applied by anyone". Every page polling `/live` was therefore handed whatever filter someone else had just applied, and one person's filter showed up on everyone's screen. A filter must belong to the viewer, and the browser tab, that applied it.

## What changed in the API

| | Before | Now |
|---|---|---|
| `GET /api/admin/live` | had `section2` | **no `section2` field**. Reading it gives `undefined`, not an error |
| Showing a filter result | read `live.section2` | show the response of **this viewer's own** `POST /api/admin/filter` (unchanged endpoint) |
| Showing it again later | — | **new** `GET /api/admin/filter/{requestId}`: same shape as the `POST` response |

`GET /api/admin/filter/{requestId}` answers:
- `200` with the result;
- `404 {"error": "request <id> not found"}`;
- `409 {"error": "request is not complete", "requestId": <id>, "status": "pending" | "processing" | "error"}` while it is still calculating, or if it failed;
- `500` on a server error.

Every call still sends `X-Api-Key` and runs server-side, as now. Nothing else in `/live` changed.

## What to change in this app

1. **Stop reading `live.section2` anywhere**: rendering, state, caching, types. Remove the field from the `/live` type so nothing can still read it.
2. **Keep the applied filter per viewer and per tab only.** Use the page's own state plus its URL. Never put it in shared storage: no Firestore document per client, no server-side "current filter", no cache key shared between users. Two people looking at the same client must be able to filter independently.
3. **On Apply**: call `POST /api/admin/filter`, show its response, and put the filter and the returned `requestId` in the page URL, e.g. `?filter=time&period=day&start=…&end=…&result=123`. Replace the URL; don't push a new history entry.
4. **When the page opens with `result=<id>` in its URL**: call `GET /api/admin/filter/{id}` and show that result. **Don't** call `POST` again: nothing is recomputed on page load.
   - On `404`: put the filter from the URL back into the filter bar, show "The saved result in this link no longer exists. Press Apply Filter to calculate it again.", and remove `result` from the URL.
   - On `409` with status `pending` or `processing`: wait a few seconds and ask again.
   - On `409` with status `error`: show "Calculation failed. Please try again."
5. **Reset**: clear the filter from the URL and go back to the default view.
6. **Defaults are the same for everyone, as before**: no filter applied, and the Filtered Parameters tiles show the Section 1 values with "No filter applied". The filter bar opens on Time Range → Yesterday, with every parameter ticked.
7. **Section 1 (lifetime tiles, refill chart, impeller current, spares) never reacts to a filter.** Leave it as it is.

## Check when done

- Open the same client in two different browsers. Apply a filter in one, and the other doesn't change, not even after its next `/live` refresh.
- Refresh a page with a filter applied, and the same result comes back without a new `POST /api/admin/filter` (check the network tab).
- Open a link whose `result` id doesn't exist: the filter is refilled and the message is shown.
- Nothing reads `section2` from `/live` any more (search the code).

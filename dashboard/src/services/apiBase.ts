// Where this app is mounted. Vite's BASE_URL always carries a trailing slash ("/shotsense/",
// or "/" at the root); everything downstream wants it stripped, so normalise once here.
// React Router in particular does a literal startsWith() against the basename, so a trailing
// slash makes the no-slash URL "/shotsense" fail to match and the router renders nothing.
export const BASE_PATH = import.meta.env.BASE_URL.replace(/\/$/, '');

// Same-origin API prefix. VITE_API_URL wins when the frontend is served from a different origin;
// otherwise it is BASE_PATH, which ASP.NET Core also uses as its PathBase under IIS sub-path
// hosting ("/shotsense/" -> "/shotsense", root "/" -> "").
export const API_BASE = (import.meta.env.VITE_API_URL as string) || BASE_PATH;

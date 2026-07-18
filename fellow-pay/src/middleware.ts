import { NextResponse } from "next/server";
import type { NextRequest } from "next/server";

const PUBLIC_PATHS = ["/signin", "/signup", "/forgot-password", "/reset-password"];

export function middleware(request: NextRequest) {
  const { pathname } = request.nextUrl;

  // Allow public paths and static assets
  if (
    PUBLIC_PATHS.some((p) => pathname.startsWith(p)) ||
    pathname.startsWith("/_next") ||
    pathname.startsWith("/api") ||
    pathname.startsWith("/images") ||
    pathname === "/favicon.ico"
  ) {
    return NextResponse.next();
  }

  // Add security headers
  const response = NextResponse.next();

  // Content Security Policy
  // Stripe.js + Payment Element are loaded from js.stripe.com and run XHR/iframes against
  // *.stripe.com / m.stripe.com — both are required for the public checkout (/pay/[token]).
  // See https://docs.stripe.com/security/guide#content-security-policy
  const apiBase = process.env.NEXT_PUBLIC_API_BASE_URL || "http://localhost:5000";
  response.headers.set(
    "Content-Security-Policy",
    [
      "default-src 'self'",
      // Cloudflare auto-injects the web-analytics beacon (cloudflareinsights.com) when
      // the site is fronted by a Cloudflare tunnel/proxy — must be explicitly allowed
      // or the browser blocks the script.
      "script-src 'self' 'unsafe-eval' 'unsafe-inline' https://js.stripe.com https://m.stripe.network https://static.cloudflareinsights.com",
      "style-src 'self' 'unsafe-inline' https://fonts.googleapis.com",
      "font-src 'self' https://fonts.gstatic.com",
      // Woovi/OpenPix serve o PNG do QR Code Pix em api.woovi(-sandbox).com e api.openpix.com.br.
      // Sem isso, o <img> do QR cai em ERR_BLOCKED_BY_CSP e renderiza só o alt-text quebrado.
      //
      // `apiBase` é incluído pra que covers de produto (e qualquer upload futuro)
      // servidos via `/api/v1/storage/...` na própria API carreguem em `<img>`.
      // Storage public é proxy via API, então precisa do mesmo origin que connect-src.
      //
      // `https:` no final é safety-net pra capas de produto vindas com URL absoluta
      // de qualquer CDN/storage externo (ex: produto importado com cover em S3
      // público). Removível se quisermos enforcement estrito.
      `img-src 'self' data: blob: ${apiBase} https://*.stripe.com https://api.woovi.com https://api.woovi-sandbox.com https://api.openpix.com.br https:`,
      `connect-src 'self' ${apiBase} https://api.stripe.com https://maps.googleapis.com https://m.stripe.network https://cloudflareinsights.com https://pay.google.com`,
      // Google Pay (via Stripe Express Checkout) renders the button + sheet inside iframes
      // hosted on pay.google.com / www.google.com. Missing those = Stripe reports
      // googlePay: false at availability check, and the wallet button doesn't render.
      "frame-src https://js.stripe.com https://hooks.stripe.com https://pay.google.com https://www.google.com",
      "frame-ancestors 'none'",
      "base-uri 'self'",
      "form-action 'self'",
    ].join("; ")
  );

  // Prevent clickjacking
  response.headers.set("X-Frame-Options", "DENY");

  // Prevent MIME type sniffing
  response.headers.set("X-Content-Type-Options", "nosniff");

  // Referrer policy
  response.headers.set("Referrer-Policy", "strict-origin-when-cross-origin");

  // Permissions policy
  response.headers.set("Permissions-Policy", "camera=(), microphone=(), geolocation=()");

  return response;
}

export const config = {
  matcher: ["/((?!_next/static|_next/image|favicon.ico).*)"],
};

/**
 * Helpers de URL — resolução de paths relativos vindos do backend pra URLs
 * absolutas compartilháveis (links de afiliado, checkout, etc).
 */

/**
 * Backend monta URLs públicas usando `Checkout:PublicBaseUrl` da config. Em
 * ambientes onde a env não está setada, o backend retorna paths relativos
 * tipo `/p/curso-sql-master?aff=ABC123` — copiar isso pra WhatsApp/redes não
 * funciona pq não tem origin.
 *
 * Esse helper resolve: se o link já tem `http(s)://`, retorna como veio (front
 * confia no backend). Senão, prepende `window.location.origin` — assume que o
 * mesmo host que serve o admin também serve o checkout público (`/p/{slug}`).
 *
 * SSR-safe: durante prerender (sem window), retorna o input como tá. Componentes
 * que renderizam URL devem ser `"use client"` pra que o origin esteja disponível
 * no momento do display.
 */
export function resolveCheckoutUrl(raw: string | null | undefined): string {
  if (!raw) return "";
  if (/^https?:\/\//i.test(raw)) return raw;
  if (typeof window === "undefined") return raw;
  // Path relativo — prepende a origin atual. Garante leading slash pra que
  // `origin + path` componha bem (ex: "/p/foo" + "https://app.com" => "https://app.com/p/foo").
  const path = raw.startsWith("/") ? raw : `/${raw}`;
  return `${window.location.origin}${path}`;
}

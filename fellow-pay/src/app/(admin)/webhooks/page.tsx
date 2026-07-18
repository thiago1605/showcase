import { redirect } from "next/navigation";

/**
 * /webhooks deprecated em 2026-05-27 — funcionalidade unificada em
 * /integrations. Antes existiam 2 pages quase idênticas: tenant-wide
 * (devs/API) e seller-scoped (producer). Pro seller eram opacas e mostravam
 * a mesma coisa. Mantemos esta route como redirect pra não quebrar
 * bookmarks/links externos.
 */
export default function WebhooksRedirect() {
  redirect("/integrations");
}

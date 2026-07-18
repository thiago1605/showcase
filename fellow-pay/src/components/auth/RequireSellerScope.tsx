"use client";

import { useEffect } from "react";
import { usePathname, useRouter } from "next/navigation";
import { useAuth } from "@/context/AuthContext";

/**
 * Wrapper que gate rotas seller-scoped (dashboard, transações etc).
 *
 * Quando o user autenticado NÃO tem `sellerId` (caso de novo user criado via
 * Google SSO antes de ser vinculado a um produtor), redireciona para
 * `/onboarding` onde ele escolhe entre Afiliado e Produtor e completa o
 * cadastro do Seller. Após o onboard concluído, tokens novos são emitidos com
 * sellerId no payload e este wrapper para de bloquear.
 *
 * Não substitui a checagem de autorização do backend — é UX. Backend continua
 * fonte da verdade via `RequireSellerScope` no HttpContext.
 */

/**
 * Paths que NÃO precisam de sellerId — user pode acessar mesmo sem vínculo.
 * Estes não fazem chamadas seller-scoped ou são parte do flow de
 * onboarding/perfil que faz sentido user ver antes de ter seller.
 *
 * Lista é de **prefixos** (startsWith). Adicione novos paths quando criar
 * features que devam funcionar para users orphãos.
 */
const PATHS_WITHOUT_SELLER_REQUIREMENT = [
  "/onboarding",     // próprio fluxo de onboarding — não bloquear
  "/profile",        // dados do user, não do seller
  "/tier",           // info de plano do user
  "/settings",       // preferências do user
  "/team",           // gestão de team (admin level)
  "/notifications",  // notificações do user
];

export function RequireSellerScope({ children }: { children: React.ReactNode }) {
  const { user, isLoading } = useAuth();
  const pathname = usePathname() ?? "";
  const router = useRouter();

  // Redireciona pro onboarding quando user autenticado sem sellerId e em
  // path que exige seller. Usamos useEffect porque router.replace não pode
  // rodar no render (Next 16 reclama). Renderiza null enquanto navega.
  const needsOnboarding =
    !isLoading &&
    !!user &&
    !user.sellerId &&
    !PATHS_WITHOUT_SELLER_REQUIREMENT.some((p) => pathname.startsWith(p));

  useEffect(() => {
    if (needsOnboarding) router.replace("/onboarding");
  }, [needsOnboarding, router]);

  // Enquanto carrega sessão, deixa o children renderizar — AuthGuard cuida.
  if (isLoading || !user) return <>{children}</>;

  // Em path seller-free OU já com seller → renderiza normalmente.
  if (user.sellerId || PATHS_WITHOUT_SELLER_REQUIREMENT.some((p) => pathname.startsWith(p))) {
    return <>{children}</>;
  }

  // Aguardando redirect — não renderiza nada do conteúdo seller-scoped.
  return null;
}

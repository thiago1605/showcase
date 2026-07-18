"use client";

import { useCallback, useEffect, useState } from "react";
import { sellerService } from "@/services/seller.service";
import { ApiError } from "@/lib/api/client";
import type { SellerTier } from "@/types";

interface UseSellerTierResult {
  tier: SellerTier | null;
  loading: boolean;
  /** Mensagem usável pelo UI quando não dá pra carregar (403 = não-seller). */
  error: string | null;
  refresh: () => Promise<void>;
}

/**
 * Carrega o tier do seller autenticado via GET /api/v1/sellers/me/tier.
 *
 * Operadores da plataforma (sem seller_id no JWT) recebem 403 — nesse caso
 * `tier` fica null e `error` traz a mensagem orientativa. Frontend deve
 * tratar como "este usuário não tem tier" e não renderizar a UI de tier.
 *
 * Pattern espelha <see cref="useSellerProfile"/> pra consistência.
 */
export function useSellerTier(): UseSellerTierResult {
  const [tier, setTier] = useState<SellerTier | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  const load = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      const data = await sellerService.getTier();
      setTier(data);
    } catch (err) {
      if (err instanceof ApiError) {
        setError(
          err.status === 403
            ? "Sua conta não está vinculada a um seller. Acesse com uma conta de seller pra ver seu nível."
            : err.message,
        );
      } else {
        setError("Não foi possível carregar seu nível.");
      }
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    load();
  }, [load]);

  return { tier, loading, error, refresh: load };
}

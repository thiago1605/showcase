"use client";

import { useCallback, useEffect, useState } from "react";
import { sellerService } from "@/services/seller.service";
import { ApiError } from "@/lib/api/client";
import type { SellerProfile, UpdateSellerProfileRequest } from "@/types";

interface UseSellerProfileResult {
  profile: SellerProfile | null;
  loading: boolean;
  error: string | null;
  refresh: () => Promise<void>;
  update: (patch: UpdateSellerProfileRequest) => Promise<SellerProfile>;
}

/**
 * Carrega o seller autenticado via GET /api/v1/sellers/me. Operadores da
 * plataforma (sem seller_id no JWT) recebem 403 — nesse caso `profile` fica
 * null e `error` tem mensagem orientativa.
 */
export function useSellerProfile(): UseSellerProfileResult {
  const [profile, setProfile] = useState<SellerProfile | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  const load = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      const data = await sellerService.getProfile();
      setProfile(data);
    } catch (err) {
      if (err instanceof ApiError) {
        setError(
          err.status === 403
            ? "Sua conta não está vinculada a um seller. Acesse com uma conta de seller pra ver o perfil."
            : err.message
        );
      } else {
        setError("Não foi possível carregar o perfil.");
      }
      setProfile(null);
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    void load();
  }, [load]);

  const update = useCallback(async (patch: UpdateSellerProfileRequest) => {
    const updated = await sellerService.updateProfile(patch);
    setProfile(updated);
    return updated;
  }, []);

  return { profile, loading, error, refresh: load, update };
}

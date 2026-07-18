import { api } from "@/lib/api/client";
import type {
  SellerProfile,
  SellerTier,
  SellerTierCode,
  UpdateSellerProfileRequest,
} from "@/types";

/**
 * Backend serializa enums como int por convenção (sem JsonStringEnumConverter).
 * SellerTier no backend: SILVER=0, GOLD=1, DIAMOND=2, BLACK=3, INFINITE=4.
 * (Position 2 era PLATINUM antes da Sprint 2 — renomeado pra DIAMOND mantendo
 * o mesmo int. Sem migration porque a column é int no DB.)
 * Mapeia int → string code pra que o front trabalhe com union type limpo.
 */
const TIER_BY_INDEX: SellerTierCode[] = [
  "SILVER",
  "GOLD",
  "DIAMOND",
  "BLACK",
  "INFINITE",
];

function normalizeTier(raw: unknown): SellerTierCode {
  if (typeof raw === "string") return raw as SellerTierCode;
  if (typeof raw === "number" && raw >= 0 && raw < TIER_BY_INDEX.length) {
    return TIER_BY_INDEX[raw];
  }
  return "SILVER"; // fallback defensivo
}

interface RawSellerTier {
  currentTier: number | string;
  tpv30dBrl: number;
  nextTier: number | string | null;
  gapToNextBrl: number | null;
  isFoundingSeller: boolean;
  foundingNumber: number | null;
}

export const sellerService = {
  async getProfile(): Promise<SellerProfile> {
    return api.get<SellerProfile>("/api/v1/sellers/me");
  },

  async updateProfile(patch: UpdateSellerProfileRequest): Promise<SellerProfile> {
    return api.patch<SellerProfile>("/api/v1/sellers/me", patch);
  },

  /**
   * Estado de tier do seller logado. Inclui current tier, gap pro próximo, e
   * marca Founding. Backend retorna profile persistido se existir; senão,
   * fallback on-the-fly por TPV30d. Front não diferencia.
   *
   * Normaliza enum int → string code (backend serializa enums como int).
   */
  async getTier(): Promise<SellerTier> {
    const raw = await api.get<RawSellerTier>("/api/v1/sellers/me/tier");
    return {
      currentTier: normalizeTier(raw.currentTier),
      tpv30dBrl: raw.tpv30dBrl,
      nextTier: raw.nextTier != null ? normalizeTier(raw.nextTier) : null,
      gapToNextBrl: raw.gapToNextBrl,
      isFoundingSeller: raw.isFoundingSeller,
      foundingNumber: raw.foundingNumber,
    };
  },
};

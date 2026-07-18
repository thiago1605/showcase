"use client";

import React, { createContext, useContext, useState, useEffect, useCallback } from "react";
import { useRouter } from "next/navigation";
import { api, ApiError } from "@/lib/api/client";
import type { LoginResponse, TokenResponse } from "@/types";

interface AuthUser {
  userId: string;
  email: string;
  name: string;
  role: string;
  sellerId?: string;
  tenantId?: string;
}

interface AuthContextType {
  user: AuthUser | null;
  isAuthenticated: boolean;
  isLoading: boolean;
  login: (email: string, password: string) => Promise<LoginResult>;
  /** Login via Google: recebe o ID Token (JWT) emitido pelo Google Identity
   *  Services e troca por sessão local. */
  loginWithGoogle: (idToken: string) => Promise<LoginResult>;
  /** Onboarding pós-SSO: cria Seller minimal vinculado ao user e atualiza
   *  a sessão com novos tokens (que carregam o sellerId no payload). */
  completeOnboarding: (input: OnboardSellerInput) => Promise<LoginResult>;
  verifyMfa: (mfaToken: string, totpCode: string) => Promise<void>;
  logout: () => void;
  refreshSession: () => Promise<boolean>;
}

export interface OnboardSellerInput {
  mode: "AFFILIATE" | "PRODUCER";
  legalName: string;
  document: string;
  tradeName?: string;
}

interface LoginResult {
  success: boolean;
  requiresMfa?: boolean;
  mfaToken?: string;
  error?: string;
}

const AuthContext = createContext<AuthContextType | undefined>(undefined);

const TOKEN_KEY = "fellow_access_token";
const REFRESH_KEY = "fellow_refresh_token";
const USER_KEY = "fellow_user";

function getStoredToken(): string | null {
  if (typeof window === "undefined") return null;
  return sessionStorage.getItem(TOKEN_KEY);
}

function getStoredRefreshToken(): string | null {
  if (typeof window === "undefined") return null;
  return sessionStorage.getItem(REFRESH_KEY);
}

function getStoredUser(): AuthUser | null {
  if (typeof window === "undefined") return null;
  const raw = sessionStorage.getItem(USER_KEY);
  if (!raw) return null;
  try {
    return JSON.parse(raw);
  } catch {
    return null;
  }
}

function storeSession(accessToken: string, refreshToken: string, user: AuthUser) {
  sessionStorage.setItem(TOKEN_KEY, accessToken);
  sessionStorage.setItem(REFRESH_KEY, refreshToken);
  sessionStorage.setItem(USER_KEY, JSON.stringify(user));
  // Successful login resets the redirect throttle (see api.client.ts loop guard).
  sessionStorage.removeItem("fellow_last_redirect");
}

function clearSession() {
  sessionStorage.removeItem(TOKEN_KEY);
  sessionStorage.removeItem(REFRESH_KEY);
  sessionStorage.removeItem(USER_KEY);
}

function decodeJwtPayload(token: string): Record<string, unknown> | null {
  try {
    const base64 = token.split(".")[1];
    const json = atob(base64.replace(/-/g, "+").replace(/_/g, "/"));
    return JSON.parse(json);
  } catch {
    return null;
  }
}

const DEV_BYPASS = process.env.NEXT_PUBLIC_DEV_BYPASS_AUTH === "true";
const DEV_USER: AuthUser = {
  userId: "dev-seller-001",
  email: "seller@fellowpay.dev",
  name: "Seller Dev",
  role: "OWNER",
  sellerId: "dev-seller-001",
};

/**
 * Reads the current seller's id from the cached AuthUser. Returns null if no session
 * (e.g. during SSR or before login). Used by services that need to call seller-scoped
 * routes that take the id in the path (`/api/v1/receipts/seller/{sellerId}`).
 *
 * Prefer routes that read seller_id from the JWT (e.g. `/api/v1/sellers/me/*`) when
 * available — they don't need this helper at all.
 */
export function getCurrentSellerId(): string | null {
  const u = getStoredUser();
  return u?.sellerId ?? null;
}

export function AuthProvider({ children }: { children: React.ReactNode }) {
  const [user, setUser] = useState<AuthUser | null>(null);
  const [isLoading, setIsLoading] = useState(true);
  const router = useRouter();

  useEffect(() => {
    if (DEV_BYPASS) {
      setUser(DEV_USER);
      setIsLoading(false);
      return;
    }
    const storedUser = getStoredUser();
    const token = getStoredToken();
    if (storedUser && token) {
      setUser(storedUser);
    }
    setIsLoading(false);
  }, []);

  const login = useCallback(async (email: string, password: string): Promise<LoginResult> => {
    try {
      const response = await api.post<LoginResponse>("/api/v1/auth/login", { email, password });

      if (response.requiresMfa) {
        return { success: false, requiresMfa: true, mfaToken: response.mfaToken };
      }

      const payload = decodeJwtPayload(response.accessToken);
      const authUser: AuthUser = {
        userId: response.userId,
        email: (payload?.email as string) || email,
        name: (payload?.name as string) || email.split("@")[0],
        role: (payload?.role as string) || "VIEWER",
        sellerId: (payload?.seller_id as string) || undefined,
        tenantId: (payload?.tenant_id as string) || undefined,
      };

      storeSession(response.accessToken, response.refreshToken, authUser);
      setUser(authUser);
      return { success: true };
    } catch (err) {
      if (err instanceof ApiError) {
        return { success: false, error: err.message };
      }
      return { success: false, error: "Erro de conexão. Tente novamente." };
    }
  }, []);

  /**
   * Login via Google Identity Services. O caller (SignInForm) obteve o
   * ID Token através do GSI popup; aqui só fazemos a troca por sessão local.
   * Backend valida o token contra GoogleAuth:ClientId e emite os mesmos
   * accessToken/refreshToken do login normal. Não passa por MFA — Google já
   * autenticou via 2FA própria.
   */
  const loginWithGoogle = useCallback(async (idToken: string): Promise<LoginResult> => {
    try {
      const response = await api.post<LoginResponse>(
        "/api/v1/auth/google-login",
        { idToken },
      );

      // Defensive — Google login não retorna requiresMfa, mas se um dia o
      // backend adicionar MFA forçado para SSO, propagamos.
      if (response.requiresMfa) {
        return { success: false, requiresMfa: true, mfaToken: response.mfaToken };
      }

      const payload = decodeJwtPayload(response.accessToken);
      const authUser: AuthUser = {
        userId: response.userId,
        email: (payload?.email as string) || "",
        name: (payload?.name as string) || "",
        role: (payload?.role as string) || "VIEWER",
        sellerId: (payload?.seller_id as string) || undefined,
        tenantId: (payload?.tenant_id as string) || undefined,
      };

      storeSession(response.accessToken, response.refreshToken, authUser);
      setUser(authUser);
      return { success: true };
    } catch (err) {
      if (err instanceof ApiError) {
        return { success: false, error: err.message };
      }
      return { success: false, error: "Erro ao autenticar com o Google. Tente novamente." };
    }
  }, []);

  /**
   * Onboarding pós-SSO: cria Seller minimal vinculado ao user. Backend retorna
   * tokens novos com sellerId no payload — substituímos a session existente
   * para destravar endpoints seller-scoped sem precisar de logout/login.
   */
  const completeOnboarding = useCallback(
    async (input: OnboardSellerInput): Promise<LoginResult> => {
      try {
        const response = await api.post<LoginResponse>("/api/v1/auth/onboard", input);

        const payload = decodeJwtPayload(response.accessToken);
        const authUser: AuthUser = {
          userId: response.userId,
          email: (payload?.email as string) || user?.email || "",
          name: (payload?.name as string) || user?.name || "",
          role: (payload?.role as string) || user?.role || "VIEWER",
          sellerId: (payload?.seller_id as string) || undefined,
          tenantId: (payload?.tenant_id as string) || user?.tenantId,
        };

        storeSession(response.accessToken, response.refreshToken, authUser);
        setUser(authUser);
        return { success: true };
      } catch (err) {
        if (err instanceof ApiError) {
          return { success: false, error: err.message };
        }
        return { success: false, error: "Erro ao concluir onboarding. Tente novamente." };
      }
    },
    [user],
  );

  const verifyMfa = useCallback(async (mfaToken: string, totpCode: string) => {
    const response = await api.post<TokenResponse>("/api/v1/auth/verify-mfa", { mfaToken, totpCode });
    const payload = decodeJwtPayload(response.accessToken);
    const authUser: AuthUser = {
      userId: response.userId,
      email: (payload?.email as string) || "",
      name: (payload?.name as string) || "",
      role: (payload?.role as string) || "VIEWER",
      sellerId: (payload?.seller_id as string) || undefined,
      tenantId: (payload?.tenant_id as string) || undefined,
    };
    storeSession(response.accessToken, response.refreshToken, authUser);
    setUser(authUser);
  }, []);

  const logout = useCallback(() => {
    api.post("/api/v1/auth/logout").catch(() => {});
    clearSession();
    setUser(null);
    router.push("/signin");
  }, [router]);

  const refreshSession = useCallback(async (): Promise<boolean> => {
    const refreshToken = getStoredRefreshToken();
    const currentUser = getStoredUser();
    if (!refreshToken || !currentUser) return false;

    try {
      const response = await api.post<TokenResponse>("/api/v1/auth/refresh", {
        userId: currentUser.userId,
        refreshToken,
      });
      storeSession(response.accessToken, response.refreshToken, currentUser);
      return true;
    } catch {
      clearSession();
      setUser(null);
      return false;
    }
  }, []);

  return (
    <AuthContext.Provider
      value={{
        user,
        isAuthenticated: !!user,
        isLoading,
        login,
        loginWithGoogle,
        completeOnboarding,
        verifyMfa,
        logout,
        refreshSession,
      }}
    >
      {children}
    </AuthContext.Provider>
  );
}

export function useAuth() {
  const context = useContext(AuthContext);
  if (!context) {
    throw new Error("useAuth must be used within AuthProvider");
  }
  return context;
}

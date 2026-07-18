import { api } from "@/lib/api/client";
import type { LoginRequest, LoginResponse, TokenResponse } from "@/types";

export const authService = {
  async login(credentials: LoginRequest): Promise<LoginResponse> {
    return api.post<LoginResponse>("/api/v1/auth/login", credentials);
  },

  async verifyMfa(mfaToken: string, totpCode: string): Promise<TokenResponse> {
    return api.post<TokenResponse>("/api/v1/auth/verify-mfa", { mfaToken, totpCode });
  },

  async refresh(userId: string, refreshToken: string): Promise<TokenResponse> {
    return api.post<TokenResponse>("/api/v1/auth/refresh", { userId, refreshToken });
  },

  async logout(): Promise<void> {
    return api.post("/api/v1/auth/logout");
  },

  async forgotPassword(email: string): Promise<void> {
    return api.post("/api/v1/auth/forgot-password", { email });
  },

  async resetPassword(email: string, token: string, newPassword: string): Promise<void> {
    return api.post("/api/v1/auth/reset-password", { email, token, newPassword });
  },
};

import { describe, it, expect, vi, beforeEach } from "vitest";

vi.mock("@/lib/api/client", () => ({
  api: {
    post: vi.fn(),
  },
}));

import { authService } from "@/services/auth.service";
import { api } from "@/lib/api/client";

describe("authService", () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  describe("login", () => {
    it("calls api.post with credentials object", async () => {
      const mockResponse = { accessToken: "token123", refreshToken: "refresh123", userId: "user1", requiresMfa: false };
      vi.mocked(api.post).mockResolvedValue(mockResponse);

      const credentials = { email: "test@example.com", password: "password123" };
      const result = await authService.login(credentials);
      expect(api.post).toHaveBeenCalledWith("/api/v1/auth/login", credentials);
      expect(result).toEqual(mockResponse);
    });
  });

  describe("forgotPassword", () => {
    it("calls api.post with email", async () => {
      vi.mocked(api.post).mockResolvedValue({});

      await authService.forgotPassword("test@example.com");
      expect(api.post).toHaveBeenCalledWith("/api/v1/auth/forgot-password", {
        email: "test@example.com",
      });
    });
  });
});

import { api } from "@/lib/api/client";
import type { TeamMember, UserRole } from "@/types";

// Wraps UsersController. The portal exposes the seller's own team — backend already
// scopes /api/v1/users by seller_id from the JWT (see UsersController.List).
//
// The backend `UserResponse` shape matches `TeamMember` field-for-field with the
// exception of `sellerId` (extra) — TypeScript happily accepts the extra property.

interface InviteMemberRequest {
  email: string;
  name: string;
  password: string;
  role: UserRole;
}

export const teamService = {
  async list(): Promise<TeamMember[]> {
    return api.get<TeamMember[]>("/api/v1/users");
  },

  async invite(data: InviteMemberRequest): Promise<TeamMember> {
    return api.post<TeamMember>("/api/v1/users", data);
  },

  async remove(memberId: string): Promise<void> {
    return api.delete<void>(`/api/v1/users/${memberId}`);
  },

  // updateRole intentionally not exposed: backend UsersController doesn't expose a
  // role-mutation endpoint yet. Add when /api/v1/users/{id} PATCH lands.
};

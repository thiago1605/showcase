const API_BASE_URL = process.env.NEXT_PUBLIC_API_BASE_URL || "http://localhost:5000";

interface RequestOptions extends Omit<RequestInit, "body"> {
  body?: unknown;
}

// Maps an HTTP status to a portal-friendly message. The seller never sees "Request failed"
// — they get something actionable. Keep narrow: only override for codes whose default
// detail is unhelpful or noisy.
function friendlyMessageFor(status: number, raw?: string): string {
  // Pass through if the backend sent a meaningful message that isn't the literal
  // "Request failed" placeholder we use as last resort.
  if (raw && raw.trim() && raw !== "Request failed") return raw;

  switch (status) {
    case 401:
      return "Sessão expirada. Faça login novamente.";
    case 403:
      return "Sem permissão para acessar este recurso.";
    case 404:
      return "Recurso não disponível no momento.";
    case 409:
      return "Conflito ao processar a operação.";
    case 422:
      return "Dados inválidos. Revise os campos e tente novamente.";
    case 500:
    case 502:
    case 503:
    case 504:
      return "Serviço indisponível. Tente novamente em instantes.";
    default:
      return raw || `Falha ao comunicar com o servidor (${status}).`;
  }
}

// FellowCore wraps every response as { success, message, data, errors } via StandardResponseFilter.
// The frontend types are written against the inner `data` shape, so we strip the envelope here.
function unwrapEnvelope<T = unknown>(json: unknown): T {
  if (
    json !== null &&
    typeof json === "object" &&
    "success" in (json as Record<string, unknown>) &&
    "data" in (json as Record<string, unknown>)
  ) {
    return (json as { data: T }).data;
  }
  return json as T;
}

class ApiClient {
  private baseUrl: string;

  constructor(baseUrl: string) {
    this.baseUrl = baseUrl;
  }

  private getToken(): string | null {
    if (typeof window === "undefined") return null;
    return sessionStorage.getItem("fellow_access_token");
  }

  private async handleTokenRefresh(): Promise<boolean> {
    if (typeof window === "undefined") return false;
    const refreshToken = sessionStorage.getItem("fellow_refresh_token");
    const userRaw = sessionStorage.getItem("fellow_user");
    if (!refreshToken || !userRaw) return false;

    try {
      const user = JSON.parse(userRaw);
      const response = await fetch(`${this.baseUrl}/api/v1/auth/refresh`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ userId: user.userId, refreshToken }),
      });

      if (!response.ok) return false;

      const json = await response.json();
      const data = unwrapEnvelope<{ accessToken: string; refreshToken: string }>(json);
      sessionStorage.setItem("fellow_access_token", data.accessToken);
      sessionStorage.setItem("fellow_refresh_token", data.refreshToken);
      return true;
    } catch {
      return false;
    }
  }

  private async request<T>(endpoint: string, options: RequestOptions = {}, retry = true): Promise<T> {
    const { body, headers: customHeaders, ...rest } = options;
    const token = this.getToken();
    const method = (rest.method || "GET").toUpperCase();

    const headers: Record<string, string> = {
      "Content-Type": "application/json",
      ...(customHeaders as Record<string, string>),
    };

    if (token) {
      headers["Authorization"] = `Bearer ${token}`;
    }

    // Backend's IdempotencyMiddleware (Api/Middlewares/Idempotency) requires every POST
    // under /api/v1 (except /webhooks, /auth, /reconciliation) to carry an Idempotency-Key.
    // Generate a per-request UUID when the caller didn't provide one — preserves the
    // ability to override via `options.headers` if the call needs strict idempotency
    // (retried payment, etc.).
    const isMutation = method === "POST" || method === "PATCH" || method === "DELETE";
    if (isMutation && !headers["Idempotency-Key"]) {
      headers["Idempotency-Key"] =
        typeof crypto !== "undefined" && crypto.randomUUID
          ? crypto.randomUUID()
          : `fp-${Date.now()}-${Math.random().toString(36).slice(2)}`;
    }

    const response = await fetch(`${this.baseUrl}${endpoint}`, {
      ...rest,
      headers,
      body: body ? JSON.stringify(body) : undefined,
    });

    if (response.status === 401 && retry) {
      const refreshed = await this.handleTokenRefresh();
      if (refreshed) {
        return this.request<T>(endpoint, options, false);
      }
      const bypass = process.env.NEXT_PUBLIC_DEV_BYPASS_AUTH === "true";
      if (typeof window !== "undefined" && !endpoint.includes("/auth/") && !bypass) {
        // Loop guard: at most one redirect per page load. If the user is already on /signin
        // (after a previous 401), or if we redirected very recently, swallow the redirect
        // and let the caller see the ApiError instead of cycling location.href.
        const alreadyOnSignin = window.location.pathname.startsWith("/signin");
        const lastRedirect = Number(sessionStorage.getItem("fellow_last_redirect") || 0);
        const recentlyRedirected = Date.now() - lastRedirect < 5000;
        if (!alreadyOnSignin && !recentlyRedirected) {
          sessionStorage.clear();
          sessionStorage.setItem("fellow_last_redirect", Date.now().toString());
          window.location.href = "/signin";
        }
      }
    }

    if (response.status === 429) {
      const retryAfter = response.headers.get("Retry-After");
      // Mensagem user-friendly — antes era "Rate limit excedido" (jargão técnico
      // de back-end). Sellers não devem ler termos de infra. Comunica intent
      // ("muitas tentativas") e ação clara ("aguarde X segundos") em PT-BR.
      const seconds = retryAfter ? Number(retryAfter) : null;
      const wait =
        seconds && Number.isFinite(seconds) && seconds > 0
          ? `${seconds} segundo${seconds === 1 ? "" : "s"}`
          : "alguns instantes";
      throw new ApiError(
        429,
        `Você fez muitas tentativas seguidas. Aguarde ${wait} e tente de novo.`,
        { retryAfter },
      );
    }

    if (!response.ok) {
      const raw = await response.json().catch(() => null);
      // Backend wraps errors as ApiResponse: { success:false, message, errors:[...] }.
      // It also occasionally returns ProblemDetails-style: { title, detail, status }.
      // Pull the most specific field we can find, then fall back to a friendly default
      // tied to the status code so the seller never sees raw "Request failed".
      const rawMessage =
        (raw?.message as string | undefined) ||
        (raw?.detail as string | undefined) ||
        (raw?.title as string | undefined) ||
        (Array.isArray(raw?.errors) && raw.errors.length > 0 && typeof raw.errors[0] === "string"
          ? (raw.errors[0] as string)
          : undefined);

      const friendly = friendlyMessageFor(response.status, rawMessage);
      throw new ApiError(response.status, friendly, raw ?? { message: friendly });
    }

    if (response.status === 204) return undefined as T;
    const json = await response.json();
    return unwrapEnvelope(json) as T;
  }

  get<T>(endpoint: string, options?: RequestOptions): Promise<T> {
    return this.request<T>(endpoint, { ...options, method: "GET" });
  }

  post<T>(endpoint: string, body?: unknown, options?: RequestOptions): Promise<T> {
    return this.request<T>(endpoint, { ...options, method: "POST", body });
  }

  patch<T>(endpoint: string, body?: unknown, options?: RequestOptions): Promise<T> {
    return this.request<T>(endpoint, { ...options, method: "PATCH", body });
  }

  put<T>(endpoint: string, body?: unknown, options?: RequestOptions): Promise<T> {
    return this.request<T>(endpoint, { ...options, method: "PUT", body });
  }

  delete<T>(endpoint: string, options?: RequestOptions): Promise<T> {
    return this.request<T>(endpoint, { ...options, method: "DELETE" });
  }
}

export class ApiError extends Error {
  status: number;
  data: unknown;

  constructor(status: number, message: string, data?: unknown) {
    super(message);
    this.status = status;
    this.data = data;
  }
}

export const api = new ApiClient(API_BASE_URL);

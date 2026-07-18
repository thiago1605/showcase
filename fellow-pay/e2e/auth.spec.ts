import { test, expect } from "@playwright/test";

test.describe("Authentication", () => {
  test("shows login page", async ({ page }) => {
    await page.goto("/signin");
    await expect(page.locator("h1")).toContainText("Entrar");
  });

  test("redirects unauthenticated user to login", async ({ page }) => {
    await page.goto("/transactions");
    // AuthGuard should redirect to /signin
    await expect(page).toHaveURL(/signin/);
  });

  test("forgot password page is accessible", async ({ page }) => {
    await page.goto("/forgot-password");
    await expect(page.locator("h1")).toContainText("Esqueceu a senha");
  });
});

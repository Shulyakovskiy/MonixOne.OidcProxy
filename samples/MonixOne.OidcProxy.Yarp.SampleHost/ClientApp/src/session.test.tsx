import type { ReactNode } from "react";
import { act, renderHook } from "@testing-library/react";
import {
  QueryClientProvider,
  focusManager,
  useQuery,
} from "@tanstack/react-query";
import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { createBffQueryClient, sessionQueryOptions } from "./session";

beforeEach(() => {
  vi.useFakeTimers();
  vi.stubGlobal("fetch", vi.fn());
  focusManager.setFocused(true);
});
afterEach(() => {
  focusManager.setFocused(undefined);
  vi.useRealTimers();
  vi.unstubAllGlobals();
});

it("inspects only a visible session and stops polling after a 401", async () => {
  // Arrange
  vi.mocked(fetch)
    .mockResolvedValueOnce(new Response(JSON.stringify({ sub: ["user-1"] })))
    .mockResolvedValueOnce(new Response(null, { status: 401 }));
  const client = createBffQueryClient();
  const wrapper = ({ children }: { children: ReactNode }) => (
    <QueryClientProvider client={client}>{children}</QueryClientProvider>
  );
  const hook = renderHook(() => useQuery(sessionQueryOptions), { wrapper });
  await act(() => vi.advanceTimersByTimeAsync(1));
  expect(hook.result.current.data).toEqual({ sub: ["user-1"] });
  // Act
  focusManager.setFocused(false);
  await act(() => vi.advanceTimersByTimeAsync(120_000));
  // Assert
  expect(fetch).toHaveBeenCalledTimes(1);
  // Act
  await act(async () => {
    focusManager.setFocused(true);
    await vi.advanceTimersByTimeAsync(1);
  });
  // Assert
  expect(hook.result.current.data).toBeNull();
  expect(fetch).toHaveBeenCalledTimes(2);
  await act(() => vi.advanceTimersByTimeAsync(120_000));
  expect(fetch).toHaveBeenCalledTimes(2);
  hook.unmount();
  client.clear();
});

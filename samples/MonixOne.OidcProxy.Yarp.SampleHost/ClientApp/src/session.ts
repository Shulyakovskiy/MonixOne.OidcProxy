import { QueryClient, queryOptions } from "@tanstack/react-query";
import { readSession } from "./bff";

export const sessionQueryKey = ["bff-session"] as const;

export const sessionQueryOptions = queryOptions({
  queryKey: sessionQueryKey,
  queryFn: ({ signal }) => readSession(signal),
  staleTime: 30_000,
  gcTime: 0,
  retry: false,
  refetchOnWindowFocus: true,
  refetchOnReconnect: true,
  // A visible authenticated SPA keeps its server session active. The BFF alone
  // refreshes credentials, and its absolute session lifetime is never extended.
  refetchInterval: (query) => (query.state.data ? 60_000 : false),
  refetchIntervalInBackground: false,
});

export function createBffQueryClient() {
  return new QueryClient({
    defaultOptions: {
      queries: { retry: false, gcTime: 0 },
      mutations: { retry: false },
    },
  });
}

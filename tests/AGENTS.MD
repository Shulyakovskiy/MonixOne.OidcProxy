# AGENTS.md

# Engineering Guidelines — Automated Tests

This file defines the mandatory testing rules for this repository.

The primary goal is not maximum test count or maximum code coverage.

The primary goal is:

* confidence in production behavior;
* simple tests;
* readable tests;
* deterministic tests;
* stable tests;
* fast feedback;
* clear failure diagnostics;
* minimal test infrastructure;
* protection of business invariants and concurrency guarantees.

Tests are executable documentation of system behavior.

Prefer clarity over cleverness.

Do not introduce abstractions, builders, fixtures, mocks, helpers, or custom test frameworks unless they make the tests materially simpler.

---

# 1. Mandatory Testing Stack

Use the following stack by default.

## Test framework

Use:

```text
xUnit v3
```

Do not introduce NUnit, MSTest, TUnit, or another test framework unless explicitly requested.

---

## Assertions

Use:

```text
Shouldly
```

Example:

```csharp
result.Status.ShouldBe(OperationStatus.Processed);
result.Amount.ShouldBe(100m);
result.ShouldNotBeNull();
```

Prefer readable Shouldly assertions over:

```csharp
Assert.True(...);
Assert.Equal(...);
Assert.NotNull(...);
```

Do not create custom assertion libraries unless there is substantial repeated domain-specific assertion logic.

---

## Mocks and substitutes

Use:

```text
NSubstitute
```

Use NSubstitute only when a dependency should actually be substituted.

Good candidates:

* external application ports;
* message publishers;
* notification senders;
* external service abstractions;
* pure application-level collaborators;
* infrastructure unavailable or undesirable in a unit test.

Example:

```csharp
var client = Substitute.For<IProcessingClient>();

client
    .ProcessAsync(
        Arg.Any<ProcessingRequest>(),
        Arg.Any<CancellationToken>())
    .Returns(response);
```

Do not mock dependencies merely because mocking is easy.

---

## PostgreSQL integration testing

Use:

```text
Testcontainers.PostgreSql
```

Database behavior must be tested against a real PostgreSQL instance.

Never use:

```text
EF Core InMemory provider
SQLite as a PostgreSQL replacement
mocked DbContext
mocked DbSet
mocked NpgsqlConnection
mocked transaction
```

when testing PostgreSQL behavior.

The following features require a real PostgreSQL integration test:

* transactions;
* isolation;
* row locking;
* SELECT FOR UPDATE;
* FOR NO KEY UPDATE;
* SKIP LOCKED;
* NOWAIT;
* advisory locks;
* unique constraints;
* foreign keys;
* partial indexes;
* JSONB;
* PostgreSQL-specific SQL;
* concurrent updates;
* ExecuteUpdateAsync;
* ExecuteDeleteAsync;
* raw SQL;
* RETURNING;
* deadlock-sensitive behavior.

---

## ASP.NET Core integration testing

Use:

```text
Microsoft.AspNetCore.Mvc.Testing
WebApplicationFactory<Program>
```

Use WebApplicationFactory when testing an HTTP request through the actual application pipeline.

Prefer testing:

```text
HTTP
→ routing
→ endpoint
→ validation
→ handler
→ persistence
→ response
```

instead of mocking every layer separately.

---

## External HTTP services

Use:

```text
WireMock.Net
```

Use WireMock.Net for integration testing HTTP clients.

The real application HTTP client should communicate with WireMock over HTTP.

Test:

* request URL;
* HTTP method;
* headers;
* authentication headers where appropriate;
* request serialization;
* response deserialization;
* HTTP 2xx;
* HTTP 4xx;
* HTTP 5xx;
* 429;
* malformed response;
* unexpected response;
* timeout behavior where relevant.

Do not mock HttpClient.

Do not mock HttpMessageHandler unless there is a very specific reason.

Prefer:

```text
Real HttpClient
      ↓
WireMock.Net
```

---

## Optional libraries

The following libraries may be introduced when they clearly simplify the test suite.

### Respawn

Use Respawn when database cleanup between integration tests becomes difficult or expensive.

Do not add it preemptively.

### Bogus

Use Bogus when tests require substantial realistic generated datasets.

Do not use random generated values for simple business scenarios.

Prefer explicit values when those values are relevant to understanding the test.

### Verify

Use Verify only for scenarios where snapshot testing provides clear value, for example:

* large JSON contracts;
* complex serialized DTOs;
* generated documents;
* large structured responses.

Do not replace normal explicit assertions with snapshots.

---

# 2. Test Project Structure

Prefer a small number of test projects.

Default structure:

```text
src/
    Application/
    Domain/
    Infrastructure/
    Api/

tests/
    Project.UnitTests/
    Project.IntegrationTests/
```

Avoid creating separate test projects for every production assembly unless there is a strong architectural reason.

---

# 3. Unit Tests

Unit tests should test behavior that can be executed without infrastructure.

Typical unit-test candidates:

* domain rules;
* domain entities;
* value objects;
* state transitions;
* calculations;
* validators;
* parsers;
* pure application services;
* business invariants.

Unit tests should normally be:

* fast;
* deterministic;
* isolated;
* synchronous unless the production API is asynchronous;
* free from database access;
* free from network access;
* free from filesystem access.

---

# 4. Integration Tests

Use integration tests whenever behavior depends on infrastructure semantics.

Examples:

```text
EF Core + PostgreSQL
Npgsql + PostgreSQL
HttpClient + WireMock
ASP.NET Core + PostgreSQL
ASP.NET Core + WireMock
```

Do not reproduce infrastructure behavior with mocks.

If production correctness depends on PostgreSQL behavior, test PostgreSQL.

If production correctness depends on HTTP serialization, perform an HTTP request.

---

# 5. Test Naming

Test names must describe behavior.

Default format:

```text
Method_Scenario_ExpectedResult
```

Examples:

```csharp
ClaimAsync_PendingOperation_ReturnsOperation()

ClaimAsync_TwoConcurrentWorkers_OperationClaimedOnce()

ApplyRevision_CreditedAmountChanged_QuarantinesOperation()

ProcessAsync_ProcessingReturnsSuccess_MarksOperationProcessed()

ProcessAsync_ProcessingReturnsFailure_SchedulesRetry()

CreateAsync_DuplicateOperationId_DoesNotCreateDuplicate()
```

A developer should understand the scenario without opening the test body.

Avoid meaningless names such as:

```text
Test1
TestSuccess
ShouldWork
HandleTest
ProcessTest
CorrectResult
```

---

# 6. Arrange / Act / Assert

Every normal test must follow the AAA structure:

```csharp
[Fact]
public void ApplyRevision_CreditedAmountChanged_QuarantinesOperation()
{
    // Arrange
    var operation = CreateCreditedOperation(
        creditedAmount: 100m);

    // Act
    operation.ApplyRevision(
        reportedAmount: 120m);

    // Assert
    operation.Status.ShouldBe(
        IncomingBankOperationStatus.Quarantined);
}
```

Always include these comments:

```csharp
// Arrange

// Act

// Assert
```

They are mandatory unless the test is so structurally different that AAA does not apply.

Do not mix Arrange and Assert logic.

Do not perform important assertions inside helper methods unless the helper exists specifically as an assertion helper.

---

# 7. Comments

Tests must be well documented, but comments must increase clarity rather than create noise.

Comment every logical test section.

Always use:

```csharp
// Arrange
// Act
// Assert
```

Add comments for:

* why a specific value matters;
* transaction boundaries;
* lock behavior;
* concurrency synchronization;
* unusual setup;
* intentional race conditions;
* retries;
* timeout behavior;
* non-obvious business invariants;
* intentionally malformed input;
* reasons for using a specific database state.

Example:

```csharp
// Hold the first transaction open so the second worker attempts
// to claim an operation while the row is still locked.
await firstWorker.WaitUntilRowLockedAsync(ct);
```

Good comment:

```csharp
// The credited amount is immutable after successful processing.
// Any later change must quarantine the operation.
```

Bad comment:

```csharp
// Set amount to 100.
operation.Amount = 100;
```

Comments should explain:

```text
WHY
```

rather than merely repeating:

```text
WHAT
```

Shared fixtures, non-obvious helpers, and test infrastructure classes should have concise XML documentation explaining their purpose.

Example:

```csharp
/// <summary>
/// Provides a shared PostgreSQL Testcontainer for integration tests.
/// Each test receives an isolated database state.
/// </summary>
public sealed class PostgreSqlFixture
{
}
```

Do not add comments that simply translate C# into English.

Readability remains more important than comment count.

---

# 8. One Test — One Behavior

Each test should primarily verify one business behavior.

Good:

```text
ClaimAsync_PendingOperation_MarksOperationProcessing
```

Bad:

```text
ClaimAsync_WorksCorrectly
```

where one test verifies:

* status;
* retry counter;
* timestamp;
* another table;
* logging;
* HTTP request;
* five unrelated edge cases.

Multiple assertions are allowed when they collectively describe one state transition.

Example:

```csharp
result.Status.ShouldBe(OperationStatus.Processing);
result.ProcessingStartedAt.ShouldNotBeNull();
result.ProcessingAttempts.ShouldBe(1);
```

These assertions describe one behavior:

```text
operation was successfully claimed
```

That is acceptable.

---

# 9. Prefer State Verification

Prefer verifying externally observable state over implementation details.

Good:

```csharp
operation.Status.ShouldBe(OperationStatus.Processed);
```

Avoid tests that depend on private implementation details.

Do not test:

* private methods directly;
* exact internal call order unless order is a requirement;
* internal variable values;
* implementation-specific helper calls.

Tests should survive harmless refactoring.

---

# 10. Mocking Rules

Mocks are a tool, not the default testing strategy.

Before creating a substitute, ask:

```text
Does the test actually need this dependency to be fake?
```

Use NSubstitute mainly at architectural boundaries.

Good:

```text
Application service
    ↓
IEmailSender → substitute
```

Good:

```text
Domain service
    ↓
IExchangeRateProvider → substitute
```

Usually bad:

```text
Handler
 ↓
mock repository
 ↓
mock DbContext
 ↓
mock DbSet
```

when the actual production behavior depends on PostgreSQL.

---

# 11. Never Mock EF Core

Do not create:

```csharp
Substitute.For<DbContext>();
Substitute.For<DbSet<Entity>>();
```

Do not mock IQueryable.

Do not construct artificial LINQ providers.

Do not attempt to emulate PostgreSQL behavior in memory.

Use:

```text
Testcontainers.PostgreSql
```

instead.

---

# 12. Database Integration Tests

Database tests must use the actual production:

```text
DbContext
EF Core mappings
Npgsql provider
migrations/schema
PostgreSQL
```

Avoid creating a simplified test-only schema.

The test database should represent production database behavior as closely as practical.

---

# 13. Database Setup

Prefer shared expensive infrastructure and isolated test state.

Starting an entirely new PostgreSQL container for every individual test is usually unnecessary.

Prefer:

```text
Test session/class collection
        ↓
PostgreSQL Testcontainer
        ↓
isolated database state per test
```

Tests must never depend on execution order.

Each test must be independently executable.

---

# 14. Database Cleanup

Every integration test must start from a known database state.

Allowed approaches:

* transaction rollback when compatible with the behavior being tested;
* explicit cleanup;
* Respawn;
* isolated schema/database where justified.

Never depend on data created by another test.

---

# 15. Migrations

Prefer running the actual database migrations used by production.

Do not maintain a second handcrafted database schema exclusively for tests unless explicitly required.

Integration tests should catch:

* invalid mappings;
* invalid constraints;
* migration problems;
* PostgreSQL-specific incompatibilities.

---

# 16. Concurrency Tests

Concurrency behavior must be tested with actual concurrency.

Do not simulate concurrency by sequential calls.

For code involving:

```text
FOR UPDATE
SKIP LOCKED
NOWAIT
advisory locks
optimistic concurrency
claim operations
worker competition
```

create multiple real database connections/DbContexts and execute operations concurrently.

Example conceptual structure:

```text
Worker A ─────┐
              ├── PostgreSQL
Worker B ─────┘
```

Tests must explicitly coordinate the relevant race condition.

Do not use arbitrary:

```csharp
Task.Delay(500);
```

to "hope" that concurrent operations overlap.

Use deterministic synchronization primitives such as:

* TaskCompletionSource;
* Barrier;
* SemaphoreSlim;
* explicit transaction coordination;
* observable database state.

---

# 17. Concurrency Assertions

For claim/worker patterns, verify invariants such as:

```text
one operation is never claimed by two workers;
locked rows are skipped correctly;
different workers can process different rows;
rollback returns work to a processable state;
retry does not create duplicate business effects;
processing attempts are counted exactly once;
final state remains consistent.
```

Example test names:

```csharp
ClaimAsync_TwoWorkers_SameOperationClaimedOnce()

ClaimAsync_FirstRowLocked_SecondWorkerClaimsNextRow()

ClaimAsync_TransactionRolledBack_OperationCanBeClaimedAgain()
```

Concurrency tests must test invariants, not timing assumptions.

---

# 18. Async Tests

Never use:

```csharp
.Result
.Wait()
.GetAwaiter().GetResult()
```

inside asynchronous tests unless there is a specific technical reason.

Use:

```csharp
await
```

all the way through.

Always propagate:

```csharp
CancellationToken
```

when production code supports cancellation.

---

# 19. Time

Do not make tests depend on the current wall-clock time when avoidable.

Avoid assertions such as:

```csharp
entity.CreatedAt.ShouldBe(DateTime.UtcNow);
```

Prefer injecting:

```text
TimeProvider
```

into production code when time is business-relevant.

Then verify deterministic timestamps.

If exact time is not relevant, use an appropriate range assertion rather than brittle equality.

---

# 20. Delays and Sleeps

Avoid:

```csharp
Thread.Sleep(...)
Task.Delay(...)
```

as synchronization mechanisms.

A test relying on arbitrary delays is probably flaky.

Use explicit synchronization instead.

A delay is acceptable only when the delay itself is part of the behavior under test.

---

# 21. HTTP Integration Tests

When testing HTTP clients, use WireMock.Net.

Test the real client implementation.

Example:

```text
ProcessingClient
       ↓
real HttpClient
       ↓
WireMock.Net
```

Verify important request properties when they are part of the contract.

Example:

```text
POST /operations
Authorization header
Content-Type
request JSON
idempotency key
```

Do not assert irrelevant implementation details.

---

# 22. HTTP Failure Scenarios

For important external integrations, cover at least the relevant scenarios:

```text
200 success
400 invalid request
401/403 authorization failure
404 when meaningful
409 when meaningful
429 throttling
500 server failure
503 temporary unavailable
timeout
invalid JSON
empty body
unexpected enum/value
```

Do not mechanically create every HTTP status test when the application treats several statuses identically.

Test distinct behaviors.

---

# 23. API Integration Tests

Use:

```text
WebApplicationFactory<Program>
```

for application-level HTTP tests.

Prefer a real request:

```csharp
var response = await client.PostAsJsonAsync(
    "/api/operations",
    request,
    cancellationToken);
```

over directly instantiating an endpoint solely to test the HTTP contract.

These tests should verify important cross-layer behavior.

Example:

```text
HTTP request
    ↓
FastEndpoints
    ↓
validation
    ↓
handler
    ↓
EF Core
    ↓
PostgreSQL
```

---

# 24. FastEndpoints

When testing FastEndpoints:

* unit-test complex validators separately when useful;
* integration-test important endpoint contracts through HTTP;
* do not test FastEndpoints framework behavior;
* test our behavior and configuration.

Do not duplicate the same assertions at every layer.

---

# 25. Test Data

Prefer explicit test data.

Good:

```csharp
const string operationId = "operation-123";
const decimal amount = 100m;
```

This makes the scenario obvious.

Avoid:

```csharp
Guid.NewGuid();
Random.Shared.Next();
Faker.Random.Int();
```

when the actual value is important to understanding the scenario.

Use generated values only when uniqueness or large datasets are required.

---

# 26. Test Data Builders

Create builders only when object construction becomes noisy.

Do not create builders for trivial DTOs.

Builder example:

```csharp
var operation = OperationBuilder
    .Pending()
    .WithOperationId("operation-123")
    .WithAmount(100m)
    .Build();
```

Builders should provide sensible defaults.

Tests should override only values relevant to the scenario.

Avoid builders that hide important state.

Bad:

```csharp
OperationBuilder.CreateMagicObjectForTest();
```

Good:

```csharp
OperationBuilder
    .Credited()
    .WithCreditedAmount(100m)
    .Build();
```

---

# 27. Object Mothers and Fixtures

Do not create large global test object factories.

Prefer:

```text
small builders
small fixtures
local helper methods
```

over:

```text
TestHelper
TestUtils
TestManager
TestFactory
TestContextHelper
```

with hundreds of unrelated methods.

Avoid "god test helpers".

---

# 28. Shared Helpers

Before extracting a helper, prefer duplication when the duplicated code makes the test easier to understand.

A small amount of test duplication is acceptable.

Use the Rule of Three:

Do not introduce a generalized helper after seeing something only once.

Consider extraction after the same meaningful setup appears roughly three times.

Test code optimizes for readability, not DRY at all costs.

---

# 29. Parameterized Tests

Use:

```csharp
[Theory]
[InlineData(...)]
```

when the same behavior must be verified for multiple inputs.

Good:

```csharp
[Theory]
[InlineData("")]
[InlineData(" ")]
[InlineData(null)]
public void Validate_InvalidOperationId_ReturnsError(string? operationId)
{
}
```

Do not combine fundamentally different business scenarios into one enormous Theory.

If failures would be difficult to understand, use separate Facts.

---

# 30. Exceptions

Prefer explicit exception assertions.

Example:

```csharp
var exception = await Should.ThrowAsync<DomainException>(
    () => sut.ExecuteAsync(ct));

exception.Code.ShouldBe("INVALID_STATE");
```

Verify meaningful domain information.

Do not assert full exception text unless the exact message is a public contract.

---

# 31. Result Types

When production code uses:

```text
Result
Result<T>
```

assert the semantic result.

Example:

```csharp
result.IsSuccess.ShouldBeTrue();
result.Value.ShouldNotBeNull();
```

For failures:

```csharp
result.IsFailure.ShouldBeTrue();
result.Error.Code.ShouldBe(ExpectedError.Code);
```

Avoid asserting irrelevant internal representation.

---

# 32. State Machine Testing

For entities with statuses such as:

```text
Pending
Processing
Processed
Failed
Quarantined
Ignored
```

tests must focus on valid and invalid state transitions.

For every important transition consider:

```text
valid source state;
invalid source state;
idempotent repeated operation;
business invariant violation;
concurrent transition;
retry after failure.
```

Do not merely test property assignments.

Test business rules.

---

# 33. Idempotency

Any operation designed to be idempotent must have explicit idempotency tests.

Typical scenarios:

```text
same request twice;
same operation ID twice;
same payload twice;
same operation after timeout;
retry after ambiguous external failure;
concurrent duplicate requests.
```

Verify business effects, not merely returned status codes.

---

# 34. Retry Behavior

Retry tests must verify:

```text
which failures are retryable;
which failures are permanent;
attempt counter;
next retry time;
maximum attempts;
final state;
no duplicated side effects.
```

Do not test a retry library's internal implementation.

Test our retry policy and business outcome.

---

# 35. Regression Tests

Every confirmed production bug should receive a regression test whenever practical.

Workflow:

```text
1. Reproduce the bug with a failing test.
2. Confirm the test fails for the expected reason.
3. Fix production code.
4. Confirm the test passes.
5. Run related regression tests.
```

Do not modify the test merely to make an incorrect implementation pass.

---

# 36. Test-First Bug Fixing

When asked to fix a bug, Codex should prefer:

```text
reproduce
→ failing test
→ implementation fix
→ passing test
→ related test suite
```

when the behavior can reasonably be tested.

---

# 37. Existing Behavior

Do not assume current production code is correct merely because it exists.

When requirements, tests, and implementation disagree:

1. identify the inconsistency;
2. determine the intended business behavior from available specifications;
3. do not silently rewrite tests to match implementation;
4. report ambiguity if business intent cannot be established.

Tests must encode intended behavior, not blindly mirror existing code.

---

# 38. Production Code Changes

Do not change production code merely to make testing convenient.

Production-code changes are acceptable when they improve the real design, for example:

```text
introducing TimeProvider;
extracting an actual infrastructure boundary;
removing static global state;
making dependencies explicit;
improving dependency injection.
```

Do not expose private implementation details solely for tests.

Avoid:

```text
public methods only for tests;
test-only production flags;
InternalsVisibleTo without justification;
production branches checking test environment.
```

---

# 39. Test Isolation

Tests must not depend on:

* execution order;
* previous tests;
* static mutable state;
* machine-local configuration;
* developer-specific environment;
* external internet access;
* production services;
* real customer data.

Tests must be repeatable locally and in CI.

---

# 40. Determinism

The same code and same test input must produce the same test result.

Eliminate unnecessary:

```text
randomness
wall-clock dependency
network dependency
race-dependent assumptions
execution-order assumptions
machine-specific paths
```

Flaky tests are defects.

Do not solve flaky tests by automatically rerunning them until they pass.

Find and remove the nondeterminism.

---

# 41. Test Readability

A test should be understandable from top to bottom without navigating through many helper layers.

Prefer:

```csharp
// Arrange
var operation = OperationBuilder
    .Pending()
    .WithAmount(100m)
    .Build();

// Act
var result = await sut.HandleAsync(operation.Id, ct);

// Assert
result.Status.ShouldBe(OperationStatus.Processing);
```

over deeply abstracted test DSLs.

Avoid designing a custom language for tests.

---

# 42. Test Length

Short tests are preferred, but clarity is more important than an arbitrary line count.

Extract infrastructure noise.

Do not extract business-relevant values.

The reader should see the important scenario directly in the test.

---

# 43. Assertions

Assertions should be specific.

Good:

```csharp
result.Status.ShouldBe(OperationStatus.Processed);
```

Less useful:

```csharp
result.ShouldNotBeNull();
```

when the important behavior is the status.

Do not create dozens of unrelated assertions in a single test.

Prefer asserting business-relevant outcomes.

---

# 44. Collections

When collection ordering is part of the contract, assert the order.

When order is irrelevant, do not accidentally make the test depend on order.

Be explicit about whether ordering matters.

---

# 45. Logging

Do not normally assert log messages.

Logging is usually an implementation/observability concern rather than business behavior.

Assert logs only when logging/auditing itself is a required behavior.

Do not make tests brittle by checking exact log wording.

---

# 46. Private Methods

Never write tests directly against private methods.

Test behavior through the public API.

If a private method contains enough independent business complexity that it is difficult to test, consider whether that logic should become a real domain abstraction.

Do not change visibility only for testing.

---

# 47. Coverage

Code coverage is a diagnostic metric, not the goal.

Do not create meaningless tests solely to increase percentage coverage.

Prioritize coverage of:

1. business invariants;
2. money-related behavior;
3. state transitions;
4. idempotency;
5. concurrency;
6. persistence;
7. external integration boundaries;
8. failure paths;
9. retry behavior;
10. regression scenarios.

100% coverage is not required.

High confidence is required.

---

# 48. What Not to Test

Do not spend time testing:

* framework internals;
* trivial auto-properties;
* trivial constructors;
* EF Core itself;
* Npgsql itself;
* ASP.NET Core routing internals;
* NSubstitute itself;
* WireMock itself;
* simple mappings with no meaningful transformation.

Test our behavior.

---

# 49. Test Dependencies

Before adding a new test package:

1. verify an existing package cannot solve the problem cleanly;
2. ensure the package provides meaningful value;
3. avoid overlapping libraries;
4. avoid adding dependencies for trivial convenience.

Default approved stack:

```text
xUnit v3
Shouldly
NSubstitute
Testcontainers.PostgreSql
Microsoft.AspNetCore.Mvc.Testing
WireMock.Net
```

Optional:

```text
Respawn
Bogus
Verify
```

Do not introduce another competing library without justification.

---

# 50. Test Infrastructure

Test infrastructure must remain boring.

Prefer:

```text
PostgreSqlFixture
IntegrationTestBase
OperationBuilder
WebApplicationFactory
WireMockFixture
```

Avoid elaborate infrastructure such as:

```text
AbstractGenericTestEnvironmentFactory
UniversalIntegrationScenarioBuilder
TestExecutionOrchestrator
```

unless complexity genuinely requires it.

Simple code is preferred.

---

# 51. Test Base Classes

Avoid deep inheritance hierarchies in tests.

Prefer composition.

At most one simple integration-test base class is generally acceptable when it removes repetitive infrastructure setup.

Do not hide scenario-specific behavior inside a base class.

---

# 52. Cancellation

When CancellationToken behavior is important, test it explicitly.

Verify cancellation does not leave inconsistent persistent state.

For transaction-processing code consider scenarios such as:

```text
cancellation before claim;
cancellation after claim;
cancellation during external request;
cancellation before commit.
```

Only add scenarios relevant to the actual consistency model.

---

# 53. Transaction Tests

Transaction-dependent behavior must use the real database.

Explicitly test important cases:

```text
commit persists all required changes;
rollback persists none;
partial state cannot escape;
concurrent transactions preserve invariants.
```

If multiple tables represent one atomic business state, verify they remain consistent after both success and failure.

---

# 54. Worker Tests

Background workers should contain minimal business logic.

Prefer testing the underlying processing service/handler directly.

Worker tests should focus only on worker-specific behavior where necessary:

* cancellation;
* orchestration;
* idle behavior;
* processing loop behavior.

Do not build complicated tests around an infinite `BackgroundService` loop when the real behavior can be tested through a single-iteration processor.

Prefer production design such as:

```text
BackgroundService
      ↓
ProcessNextAsync()
      ↓
business handlers
```

Then test `ProcessNextAsync()` independently.

---

# 55. Background Processing Invariants

For durable processing systems verify:

```text
Pending → Processing
Processing → Processed
Processing → retryable state
Processing → Failed
Processing → Quarantined
```

where applicable.

Also verify:

```text
processing_started_at;
processing_attempts;
next_attempt_at;
stale Processing recovery;
idempotent retries;
duplicate prevention.
```

---

# 56. External Side Effects

When a workflow performs an external side effect and updates the database, explicitly consider the failure window:

```text
DB state change
→ external request
→ process crash
→ retry
```

Tests should verify the chosen idempotency strategy where that risk exists.

---

# 57. Test Review Checklist

Before completing test work, Codex must review the tests and ask internally:

```text
Is the behavior clear from the test name?

Is the Arrange section minimal?

Is the Act section obvious?

Are assertions business-relevant?

Did I mock something that should use real infrastructure?

Can this test become flaky?

Does it depend on timing?

Does it depend on execution order?

Did I add an unnecessary abstraction?

Did I duplicate a framework feature?

Would another developer understand why this test exists?

Does the test verify the requirement rather than the current implementation?
```

Fix issues before considering the task complete.

---

# 58. Required Workflow for Codex

When asked to add tests:

## Step 1 — Understand

Read:

* relevant production code;
* nearby existing tests;
* domain rules;
* database mappings;
* related interfaces;
* existing test infrastructure.

Do not start by generating boilerplate.

---

## Step 2 — Identify behaviors

List the behaviors that materially require protection.

Prioritize:

```text
happy path
business invariants
edge cases
failure paths
concurrency
idempotency
transaction behavior
regressions
```

Do not create tests for every implementation branch mechanically.

---

## Step 3 — Choose test level

Use the cheapest test level that still verifies the real behavior.

Decision rule:

```text
Pure business rule
        → Unit test

Application orchestration
        → Unit test when infrastructure semantics are irrelevant

PostgreSQL semantics
        → Integration test + Testcontainers

HTTP client contract
        → Integration test + WireMock.Net

Full API behavior
        → WebApplicationFactory

Concurrency / locks
        → Real PostgreSQL integration test
```

---

## Step 4 — Implement simply

Prefer the simplest implementation that clearly expresses the scenario.

Do not create infrastructure in anticipation of hypothetical future tests.

---

## Step 5 — Run tests

After changing tests or production code, run the relevant test project.

Default:

```bash
dotnet test
```

When possible, first run the narrow relevant test set and then the broader suite.

Example:

```bash
dotnet test tests/Project.UnitTests
dotnet test tests/Project.IntegrationTests
dotnet test
```

Use the actual repository project names.

Do not claim tests pass unless they were actually executed successfully.

---

## Step 6 — Review failures

When a test fails:

1. understand why;
2. determine whether production code or test expectations are wrong;
3. do not weaken assertions merely to get green tests;
4. do not increase arbitrary delays;
5. do not blindly add mocks.

---

## Step 7 — Review diff

Before finishing:

* review changed production code;
* review changed test code;
* remove unused helpers;
* remove duplication that genuinely harms readability;
* remove unnecessary abstractions;
* check naming;
* check comments;
* check deterministic behavior;
* verify no test accidentally depends on another test.

---

# 59. Definition of Done

Test-related work is complete only when:

```text
[ ] Relevant behavior is covered.

[ ] Important business invariants are covered.

[ ] Failure scenarios are covered where meaningful.

[ ] Concurrency is tested using real concurrency where required.

[ ] PostgreSQL-specific behavior uses real PostgreSQL.

[ ] External HTTP behavior uses WireMock.Net where appropriate.

[ ] EF Core is not mocked.

[ ] DbContext/DbSet are not mocked.

[ ] Tests use Shouldly.

[ ] NSubstitute is used only at appropriate boundaries.

[ ] Test names describe behavior.

[ ] Tests follow Arrange / Act / Assert.

[ ] Logical test sections are commented.

[ ] Non-obvious decisions include WHY comments.

[ ] Tests are deterministic.

[ ] Tests do not depend on execution order.

[ ] No arbitrary delay is used for synchronization.

[ ] No unnecessary test abstraction was introduced.

[ ] Relevant tests were actually executed.

[ ] All executed tests pass.

[ ] The final diff was reviewed.
```

---

# 60. Primary Principle

When choosing between a sophisticated test architecture and a simple readable test, choose the simple readable test.

When choosing between mocking real database behavior and running PostgreSQL, run PostgreSQL.

When choosing between testing implementation details and testing a business invariant, test the business invariant.

When choosing between reducing duplication and making a test self-explanatory, prefer the self-explanatory test.

Tests should make production behavior easier to understand, not harder.

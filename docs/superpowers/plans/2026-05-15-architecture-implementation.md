# DistSharp Architecture Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Scaffold the DistSharp solution and implement all core abstractions, the channel-based pipeline executor, and the file checkpoint store.

**Architecture:** Multi-project solution (`DistSharp.Core`, `.Roslyn`, `.Providers`, `.Cli`) connected by interface boundaries. `DistSharp.Core` owns all abstractions and the pipeline executor; concrete implementations of `ISolutionAnalyzer` and `ILlmProvider` live in separate projects. The pipeline executor wires steps together via `System.Threading.Channels`, with one output channel per step and forwarder tasks that handle fan-out (copy-to-N) and fan-in (countdown latch).

**Tech Stack:** .NET 10, `System.Threading.Channels`, `Microsoft.Extensions.DependencyInjection.Abstractions`, `Microsoft.Extensions.Logging.Abstractions`, `Microsoft.Extensions.Options`, xUnit, NSubstitute, FluentAssertions, StyleCop.Analyzers, Central Package Management.

---

## File Map

```
/                                              ← repo root
├── DistSharp.sln
├── global.json
├── Directory.Build.props
├── Directory.Packages.props
├── .editorconfig
├── stylecop.json
├── azure-pipelines.yml
│
├── src/
│   ├── DistSharp.Core/
│   │   ├── DistSharp.Core.csproj
│   │   ├── Abstractions/
│   │   │   ├── ICheckpointStore.cs
│   │   │   ├── IDatasetWriter.cs
│   │   │   ├── IDatasetWriterFactory.cs
│   │   │   ├── ILlmProvider.cs
│   │   │   ├── ILlmProviderFactory.cs
│   │   │   └── ISolutionAnalyzer.cs
│   │   ├── Checkpointing/
│   │   │   └── FileCheckpointStore.cs
│   │   ├── Configuration/
│   │   │   ├── OutputConfig.cs
│   │   │   ├── PipelineConfig.cs
│   │   │   ├── SolutionConfig.cs
│   │   │   └── StepConfig.cs
│   │   ├── Models/
│   │   │   ├── ChatMessage.cs
│   │   │   ├── ChatRole.cs
│   │   │   ├── ExtractedSymbol.cs
│   │   │   ├── LlmRequestOptions.cs
│   │   │   ├── PipelineCheckpoint.cs
│   │   │   ├── Row.cs
│   │   │   └── SolutionAnalysisOptions.cs
│   │   └── Pipeline/
│   │       ├── IPipelineExecutor.cs
│   │       ├── IStep.cs
│   │       ├── PipelineDefinition.cs
│   │       ├── PipelineExecutor.cs
│   │       └── StepDefinition.cs
│   ├── DistSharp.Roslyn/
│   │   └── DistSharp.Roslyn.csproj
│   ├── DistSharp.Providers/
│   │   └── DistSharp.Providers.csproj
│   └── DistSharp.Cli/
│       └── DistSharp.Cli.csproj
│
└── tests/
    ├── DistSharp.Core.Tests/
    │   ├── DistSharp.Core.Tests.csproj
    │   ├── Checkpointing/
    │   │   └── FileCheckpointStoreTests.cs
    │   ├── Models/
    │   │   └── RowTests.cs
    │   └── Pipeline/
    │       └── PipelineExecutorTests.cs
    ├── DistSharp.Roslyn.Tests/
    │   └── DistSharp.Roslyn.Tests.csproj
    ├── DistSharp.Providers.Tests/
    │   └── DistSharp.Providers.Tests.csproj
    └── DistSharp.Cli.Tests/
        └── DistSharp.Cli.Tests.csproj
```

---

## Task 1: Initialize solution structure

**Files:**
- Create: `global.json`
- Create: `DistSharp.sln`
- Create: `Directory.Build.props`
- Create: `Directory.Packages.props`
- Create: `src/DistSharp.Core/DistSharp.Core.csproj`
- Create: `src/DistSharp.Roslyn/DistSharp.Roslyn.csproj`
- Create: `src/DistSharp.Providers/DistSharp.Providers.csproj`
- Create: `src/DistSharp.Cli/DistSharp.Cli.csproj`
- Create: `tests/DistSharp.Core.Tests/DistSharp.Core.Tests.csproj`
- Create: `tests/DistSharp.Roslyn.Tests/DistSharp.Roslyn.Tests.csproj`
- Create: `tests/DistSharp.Providers.Tests/DistSharp.Providers.Tests.csproj`
- Create: `tests/DistSharp.Cli.Tests/DistSharp.Cli.Tests.csproj`

- [ ] **Step 1: Create solution and project scaffolding**

```bash
cd c:/Development/DistSharp
dotnet new sln -n DistSharp
mkdir -p src/DistSharp.Core src/DistSharp.Roslyn src/DistSharp.Providers src/DistSharp.Cli
mkdir -p tests/DistSharp.Core.Tests tests/DistSharp.Roslyn.Tests tests/DistSharp.Providers.Tests tests/DistSharp.Cli.Tests
```

- [ ] **Step 2: Create `global.json`**

```json
{
  "sdk": {
    "version": "10.0.100",
    "rollForward": "latestPatch"
  }
}
```

Verify the version matches your installed SDK: `dotnet --version`. Adjust if needed.

- [ ] **Step 3: Create `Directory.Build.props`**

```xml
<Project>
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <LangVersion>latest</LangVersion>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="StyleCop.Analyzers" PrivateAssets="all" />
  </ItemGroup>

  <PropertyGroup Condition="$(MSBuildProjectName.EndsWith('.Tests'))">
    <IsPackable>false</IsPackable>
    <!-- Relax SA1600 (public members must have docs) in test projects -->
    <NoWarn>$(NoWarn);SA1600;SA1601;SA1602</NoWarn>
  </PropertyGroup>
</Project>
```

- [ ] **Step 4: Create `Directory.Packages.props`**

> Verify latest stable versions on NuGet before committing. These are approximate versions for .NET 10 era.

```xml
<Project>
  <PropertyGroup>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
  </PropertyGroup>

  <ItemGroup Label="Runtime">
    <PackageVersion Include="Microsoft.Extensions.DependencyInjection.Abstractions" Version="10.0.0" />
    <PackageVersion Include="Microsoft.Extensions.Hosting" Version="10.0.0" />
    <PackageVersion Include="Microsoft.Extensions.Logging.Abstractions" Version="10.0.0" />
    <PackageVersion Include="Microsoft.Extensions.Options" Version="10.0.0" />
  </ItemGroup>

  <ItemGroup Label="Analyzers">
    <PackageVersion Include="StyleCop.Analyzers" Version="1.2.0-beta.507" />
  </ItemGroup>

  <ItemGroup Label="Testing">
    <PackageVersion Include="coverlet.collector" Version="6.0.4" />
    <PackageVersion Include="FluentAssertions" Version="6.12.0" />
    <PackageVersion Include="Microsoft.NET.Test.Sdk" Version="17.12.0" />
    <PackageVersion Include="NSubstitute" Version="5.3.0" />
    <PackageVersion Include="xunit" Version="2.9.3" />
    <PackageVersion Include="xunit.runner.visualstudio" Version="2.8.2" />
  </ItemGroup>
</Project>
```

- [ ] **Step 5: Create the four source project files**

`src/DistSharp.Core/DistSharp.Core.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <PackageReference Include="Microsoft.Extensions.DependencyInjection.Abstractions" />
    <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" />
    <PackageReference Include="Microsoft.Extensions.Options" />
  </ItemGroup>
</Project>
```

`src/DistSharp.Roslyn/DistSharp.Roslyn.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <ProjectReference Include="..\DistSharp.Core\DistSharp.Core.csproj" />
  </ItemGroup>
</Project>
```

`src/DistSharp.Providers/DistSharp.Providers.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <ProjectReference Include="..\DistSharp.Core\DistSharp.Core.csproj" />
  </ItemGroup>
</Project>
```

`src/DistSharp.Cli/DistSharp.Cli.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <PackAsTool>true</PackAsTool>
    <ToolCommandName>distsharp</ToolCommandName>
    <PackageId>DistSharp</PackageId>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\DistSharp.Core\DistSharp.Core.csproj" />
    <ProjectReference Include="..\DistSharp.Roslyn\DistSharp.Roslyn.csproj" />
    <ProjectReference Include="..\DistSharp.Providers\DistSharp.Providers.csproj" />
    <PackageReference Include="Microsoft.Extensions.Hosting" />
  </ItemGroup>
</Project>
```

Create placeholder `Program.cs` in `src/DistSharp.Cli/`:
```csharp
// Entry point — implemented in the CLI phase.
```

- [ ] **Step 6: Create the four test project files**

`tests/DistSharp.Core.Tests/DistSharp.Core.Tests.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <ProjectReference Include="..\..\src\DistSharp.Core\DistSharp.Core.csproj" />
    <PackageReference Include="coverlet.collector" />
    <PackageReference Include="FluentAssertions" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" />
    <PackageReference Include="NSubstitute" />
    <PackageReference Include="xunit" />
    <PackageReference Include="xunit.runner.visualstudio" />
  </ItemGroup>
</Project>
```

`tests/DistSharp.Roslyn.Tests/DistSharp.Roslyn.Tests.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <ProjectReference Include="..\..\src\DistSharp.Roslyn\DistSharp.Roslyn.csproj" />
    <PackageReference Include="coverlet.collector" />
    <PackageReference Include="FluentAssertions" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" />
    <PackageReference Include="NSubstitute" />
    <PackageReference Include="xunit" />
    <PackageReference Include="xunit.runner.visualstudio" />
  </ItemGroup>
</Project>
```

`tests/DistSharp.Providers.Tests/DistSharp.Providers.Tests.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <ProjectReference Include="..\..\src\DistSharp.Providers\DistSharp.Providers.csproj" />
    <PackageReference Include="coverlet.collector" />
    <PackageReference Include="FluentAssertions" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" />
    <PackageReference Include="NSubstitute" />
    <PackageReference Include="xunit" />
    <PackageReference Include="xunit.runner.visualstudio" />
  </ItemGroup>
</Project>
```

`tests/DistSharp.Cli.Tests/DistSharp.Cli.Tests.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <ProjectReference Include="..\..\src\DistSharp.Cli\DistSharp.Cli.csproj" />
    <PackageReference Include="coverlet.collector" />
    <PackageReference Include="FluentAssertions" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" />
    <PackageReference Include="NSubstitute" />
    <PackageReference Include="xunit" />
    <PackageReference Include="xunit.runner.visualstudio" />
  </ItemGroup>
</Project>
```

- [ ] **Step 7: Add all projects to the solution**

```bash
dotnet sln add src/DistSharp.Core/DistSharp.Core.csproj
dotnet sln add src/DistSharp.Roslyn/DistSharp.Roslyn.csproj
dotnet sln add src/DistSharp.Providers/DistSharp.Providers.csproj
dotnet sln add src/DistSharp.Cli/DistSharp.Cli.csproj
dotnet sln add tests/DistSharp.Core.Tests/DistSharp.Core.Tests.csproj
dotnet sln add tests/DistSharp.Roslyn.Tests/DistSharp.Roslyn.Tests.csproj
dotnet sln add tests/DistSharp.Providers.Tests/DistSharp.Providers.Tests.csproj
dotnet sln add tests/DistSharp.Cli.Tests/DistSharp.Cli.Tests.csproj
```

- [ ] **Step 8: Verify the solution builds**

```bash
dotnet build
```

Expected: `Build succeeded.` with 0 errors. Fix any version conflicts in `Directory.Packages.props` before proceeding.

- [ ] **Step 9: Commit**

```bash
git add -A
git commit -m "feat: initialize solution structure with 4 source and 4 test projects"
```

---

## Task 2: Configure code style, .editorconfig, and CI

**Files:**
- Create: `.editorconfig`
- Create: `stylecop.json`
- Create: `azure-pipelines.yml`

- [ ] **Step 1: Create `.editorconfig`**

```ini
root = true

[*]
indent_style = space
end_of_line = crlf
charset = utf-8
trim_trailing_whitespace = true
insert_final_newline = true

[*.cs]
indent_size = 4
csharp_style_var_for_built_in_types = false:suggestion
csharp_style_var_when_type_is_apparent = true:suggestion
csharp_style_var_elsewhere = false:suggestion
csharp_new_line_before_open_brace = all
csharp_new_line_before_else = true
csharp_new_line_before_catch = true
csharp_new_line_before_finally = true
csharp_indent_case_contents = true
csharp_indent_switch_labels = true
dotnet_sort_system_directives_first = true
dotnet_separate_import_directive_groups = false

[*.{csproj,props,targets}]
indent_size = 2

[*.{json,yml,yaml}]
indent_size = 2

[*.md]
trim_trailing_whitespace = false
```

- [ ] **Step 2: Create `stylecop.json`**

```json
{
  "$schema": "https://raw.githubusercontent.com/DotNetAnalyzers/StyleCopAnalyzers/master/StyleCop.Analyzers/StyleCop.Analyzers/Settings/stylecop.schema.json",
  "settings": {
    "documentationRules": {
      "companyName": "DistSharp",
      "xmlHeader": false,
      "documentInternalElements": false
    },
    "orderingRules": {
      "usingDirectivesPlacement": "outsideNamespace",
      "systemUsingDirectivesFirst": true
    },
    "layoutRules": {
      "newlineAtEndOfFile": "require"
    }
  }
}
```

Add `stylecop.json` to `Directory.Build.props` so all projects pick it up:

```xml
<!-- Add this ItemGroup inside Directory.Build.props -->
<ItemGroup>
  <AdditionalFiles Include="$(MSBuildThisFileDirectory)stylecop.json" />
</ItemGroup>
```

- [ ] **Step 3: Create `azure-pipelines.yml`**

```yaml
trigger:
  branches:
    include:
      - main
      - feature/*
      - bugfix/*

pool:
  vmImage: ubuntu-latest

variables:
  DOTNET_VERSION: 10.0.x
  DOTNET_SKIP_FIRST_TIME_EXPERIENCE: true
  DOTNET_NOLOGO: true

steps:
  - task: UseDotNet@2
    displayName: Install .NET SDK
    inputs:
      version: $(DOTNET_VERSION)

  - script: dotnet restore
    displayName: Restore NuGet packages

  - script: dotnet build --no-restore --configuration Release
    displayName: Build

  - script: >
      dotnet test --no-build --configuration Release
      --logger trx
      --collect "XPlat Code Coverage"
      --results-directory $(Agent.TempDirectory)/TestResults
    displayName: Test

  - task: PublishTestResults@2
    displayName: Publish test results
    condition: succeededOrFailed()
    inputs:
      testResultsFormat: VSTest
      testResultsFiles: $(Agent.TempDirectory)/TestResults/**/*.trx
```

- [ ] **Step 4: Verify build still succeeds with StyleCop**

```bash
dotnet build
```

Expected: `Build succeeded.` If StyleCop reports errors, fix them before proceeding (common: missing newline at end of file, or `Program.cs` needs a proper namespace).

- [ ] **Step 5: Commit**

```bash
git add .editorconfig stylecop.json azure-pipelines.yml Directory.Build.props
git commit -m "feat: add .editorconfig, StyleCop config, and Azure Pipelines CI skeleton"
```

---

## Task 3: Implement `Row` (TDD)

**Files:**
- Create: `src/DistSharp.Core/Models/Row.cs`
- Create: `tests/DistSharp.Core.Tests/Models/RowTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `tests/DistSharp.Core.Tests/Models/RowTests.cs`:

```csharp
using DistSharp.Core.Models;
using FluentAssertions;

namespace DistSharp.Core.Tests.Models;

public sealed class RowTests
{
    [Fact]
    public void Empty_HasNoFields()
    {
        Row.Empty.Fields.Should().BeEmpty();
    }

    [Fact]
    public void With_AddsNewField_ReturnsNewRow()
    {
        var original = Row.Empty;

        var result = original.With("key", "value");

        result.Fields.Should().ContainKey("key").WhoseValue.Should().Be("value");
        original.Fields.Should().BeEmpty(); // original is unchanged
    }

    [Fact]
    public void With_OverwritesExistingField_ReturnsNewRow()
    {
        var row = Row.Empty.With("key", "old");

        var result = row.With("key", "new");

        result.Get<string>("key").Should().Be("new");
        row.Get<string>("key").Should().Be("old"); // original unchanged
    }

    [Fact]
    public void With_Dictionary_MergesAllFields()
    {
        var row = Row.Empty.With("a", 1);

        var result = row.With(new Dictionary<string, object?> { ["b"] = 2, ["c"] = 3 });

        result.Fields.Should().HaveCount(3);
        result.Get<int>("a").Should().Be(1);
        result.Get<int>("b").Should().Be(2);
        result.Get<int>("c").Should().Be(3);
    }

    [Fact]
    public void Get_ReturnsTypedValue_WhenKeyExistsAndTypeMatches()
    {
        var row = Row.Empty.With("count", 42);

        row.Get<int>("count").Should().Be(42);
    }

    [Fact]
    public void Get_ReturnsDefault_WhenKeyMissing()
    {
        Row.Empty.Get<string>("missing").Should().BeNull();
    }

    [Fact]
    public void TryGet_ReturnsTrueAndValue_WhenKeyExistsAndTypeMatches()
    {
        var row = Row.Empty.With("x", 99);

        var found = row.TryGet<int>("x", out var value);

        found.Should().BeTrue();
        value.Should().Be(99);
    }

    [Fact]
    public void TryGet_ReturnsFalse_WhenKeyMissing()
    {
        var found = Row.Empty.TryGet<string>("missing", out var value);

        found.Should().BeFalse();
        value.Should().BeNull();
    }
}
```

- [ ] **Step 2: Run to verify the tests fail**

```bash
dotnet test tests/DistSharp.Core.Tests --filter "FullyQualifiedName~RowTests"
```

Expected: build error — `Row` does not exist yet.

- [ ] **Step 3: Implement `Row`**

Create `src/DistSharp.Core/Models/Row.cs`:

```csharp
using System.Collections.Immutable;

namespace DistSharp.Core.Models;

/// <summary>The fundamental unit of data flowing through a pipeline. Immutable.</summary>
public sealed record Row(IReadOnlyDictionary<string, object?> Fields)
{
    /// <summary>Gets an empty row with no fields.</summary>
    public static Row Empty { get; } = new(ImmutableDictionary<string, object?>.Empty);

    /// <summary>Returns the value of <paramref name="key"/> cast to <typeparamref name="T"/>, or <see langword="default"/> if missing or the wrong type.</summary>
    /// <typeparam name="T">The expected value type.</typeparam>
    /// <param name="key">The field name.</param>
    /// <returns>The typed value, or <see langword="default"/>.</returns>
    public T? Get<T>(string key)
    {
        if (Fields.TryGetValue(key, out var value) && value is T typed)
            return typed;
        return default;
    }

    /// <summary>Attempts to retrieve the value of <paramref name="key"/> as <typeparamref name="T"/>.</summary>
    /// <typeparam name="T">The expected value type.</typeparam>
    /// <param name="key">The field name.</param>
    /// <param name="value">The typed value if found.</param>
    /// <returns><see langword="true"/> if the key exists and the value is of type <typeparamref name="T"/>.</returns>
    public bool TryGet<T>(string key, out T? value)
    {
        if (Fields.TryGetValue(key, out var raw) && raw is T typed)
        {
            value = typed;
            return true;
        }

        value = default;
        return false;
    }

    /// <summary>Returns a new <see cref="Row"/> with <paramref name="key"/> set to <paramref name="value"/>.</summary>
    /// <param name="key">The field name.</param>
    /// <param name="value">The field value.</param>
    /// <returns>A new row.</returns>
    public Row With(string key, object? value) =>
        new(((ImmutableDictionary<string, object?>)Fields).SetItem(key, value));

    /// <summary>Returns a new <see cref="Row"/> with all entries from <paramref name="fields"/> merged in.</summary>
    /// <param name="fields">Fields to add or overwrite.</param>
    /// <returns>A new row.</returns>
    public Row With(IReadOnlyDictionary<string, object?> fields)
    {
        var builder = (ImmutableDictionary<string, object?>)Fields;
        foreach (var kvp in fields)
            builder = builder.SetItem(kvp.Key, kvp.Value);
        return new Row(builder);
    }
}
```

> **Note:** `Row.Empty` uses `ImmutableDictionary` internally. `With` operations stay on `ImmutableDictionary` to guarantee structural immutability (no cast-and-mutate possible). The `Fields` property is typed as `IReadOnlyDictionary` to keep the public API clean.

- [ ] **Step 4: Run tests to verify they pass**

```bash
dotnet test tests/DistSharp.Core.Tests --filter "FullyQualifiedName~RowTests"
```

Expected: `8 passed`.

- [ ] **Step 5: Commit**

```bash
git add src/DistSharp.Core/Models/Row.cs tests/DistSharp.Core.Tests/Models/RowTests.cs
git commit -m "feat: implement Row — immutable pipeline data unit"
```

---

## Task 4: Implement remaining data models

**Files:**
- Create: `src/DistSharp.Core/Models/ChatMessage.cs`
- Create: `src/DistSharp.Core/Models/ChatRole.cs`
- Create: `src/DistSharp.Core/Models/LlmRequestOptions.cs`
- Create: `src/DistSharp.Core/Models/ExtractedSymbol.cs`
- Create: `src/DistSharp.Core/Models/SolutionAnalysisOptions.cs`
- Create: `src/DistSharp.Core/Models/PipelineCheckpoint.cs`

These are plain data types with no logic — no unit tests needed beyond confirming the solution builds.

- [ ] **Step 1: Create `ChatRole` and `ChatMessage`**

`src/DistSharp.Core/Models/ChatRole.cs`:
```csharp
namespace DistSharp.Core.Models;

/// <summary>The role of a participant in a chat conversation.</summary>
public enum ChatRole
{
    /// <summary>The system prompt role.</summary>
    System,

    /// <summary>The human turn.</summary>
    User,

    /// <summary>The model turn.</summary>
    Assistant,
}
```

`src/DistSharp.Core/Models/ChatMessage.cs`:
```csharp
namespace DistSharp.Core.Models;

/// <summary>A single message in a chat conversation.</summary>
/// <param name="Role">The role of the message sender.</param>
/// <param name="Content">The text content of the message.</param>
public sealed record ChatMessage(ChatRole Role, string Content);
```

- [ ] **Step 2: Create `LlmRequestOptions`**

`src/DistSharp.Core/Models/LlmRequestOptions.cs`:
```csharp
namespace DistSharp.Core.Models;

/// <summary>Per-request options passed to an <see cref="Abstractions.ILlmProvider"/>.</summary>
public sealed class LlmRequestOptions
{
    /// <summary>Gets or initialises the model identifier. When <see langword="null"/>, the provider uses its configured default.</summary>
    public string? Model { get; init; }

    /// <summary>Gets or initialises the sampling temperature. When <see langword="null"/>, the provider uses its default.</summary>
    public float? Temperature { get; init; }

    /// <summary>Gets or initialises the maximum number of tokens to generate. When <see langword="null"/>, the provider uses its default.</summary>
    public int? MaxTokens { get; init; }
}
```

- [ ] **Step 3: Create `ExtractedSymbol` and `SolutionAnalysisOptions`**

`src/DistSharp.Core/Models/ExtractedSymbol.cs`:
```csharp
namespace DistSharp.Core.Models;

/// <summary>
/// A .NET symbol extracted from a solution by <see cref="Abstractions.ISolutionAnalyzer"/>.
/// Contains no Roslyn types so it can be consumed by Core steps without a Roslyn dependency.
/// </summary>
public sealed record ExtractedSymbol
{
    /// <summary>Gets the fully qualified name, e.g. <c>MyApp.Services.OrderService.PlaceOrderAsync</c>.</summary>
    public required string FullyQualifiedName { get; init; }

    /// <summary>Gets the full method or property signature text.</summary>
    public required string SignatureText { get; init; }

    /// <summary>Gets the cleaned source of the method or property body.</summary>
    public required string BodyText { get; init; }

    /// <summary>Gets the existing XML doc comment, or <see langword="null"/> if absent.</summary>
    public string? XmlDocComment { get; init; }

    /// <summary>Gets the name of the containing type.</summary>
    public required string ContainingType { get; init; }

    /// <summary>Gets the containing namespace.</summary>
    public required string Namespace { get; init; }

    /// <summary>Gets the relative source file path.</summary>
    public required string FilePath { get; init; }

    /// <summary>Gets the cyclomatic complexity score.</summary>
    public int Complexity { get; init; }

    /// <summary>Gets the symbol kind: <c>method</c>, <c>property</c>, <c>class</c>, or <c>interface</c>.</summary>
    public required string Kind { get; init; }
}
```

`src/DistSharp.Core/Models/SolutionAnalysisOptions.cs`:
```csharp
namespace DistSharp.Core.Models;

/// <summary>Options that control how <see cref="Abstractions.ISolutionAnalyzer"/> processes a solution.</summary>
public sealed class SolutionAnalysisOptions
{
    /// <summary>Gets or sets a value indicating whether test projects are included. Default: <see langword="false"/>.</summary>
    public bool IncludeTests { get; set; }

    /// <summary>Gets or sets a value indicating whether auto-generated (<c>*.g.cs</c>) files are included. Default: <see langword="false"/>.</summary>
    public bool IncludeGenerated { get; set; }

    /// <summary>Gets or sets the minimum cyclomatic complexity for a method to be included. Default: 3.</summary>
    public int MinComplexity { get; set; } = 3;

    /// <summary>Gets or sets namespace prefixes to exclude from analysis.</summary>
    public IReadOnlyList<string> ExcludeNamespaces { get; set; } = [];
}
```

- [ ] **Step 4: Create `PipelineCheckpoint`**

`src/DistSharp.Core/Models/PipelineCheckpoint.cs`:
```csharp
namespace DistSharp.Core.Models;

/// <summary>A point-in-time snapshot of pipeline progress, used to resume interrupted runs.</summary>
/// <param name="PipelineId">Stable identifier for the pipeline run (typically derived from the output directory path).</param>
/// <param name="StepName">Name of the step at which the checkpoint was saved.</param>
/// <param name="RowsWritten">Number of rows successfully written to the dataset at this checkpoint.</param>
/// <param name="SavedAt">UTC timestamp when the checkpoint was saved.</param>
public sealed record PipelineCheckpoint(
    string PipelineId,
    string StepName,
    long RowsWritten,
    DateTimeOffset SavedAt);
```

- [ ] **Step 5: Verify the solution builds**

```bash
dotnet build
```

Expected: `Build succeeded.`

- [ ] **Step 6: Commit**

```bash
git add src/DistSharp.Core/Models/
git commit -m "feat: add ChatMessage, ExtractedSymbol, LlmRequestOptions, PipelineCheckpoint data models"
```

---

## Task 5: Define core interfaces and pipeline types

**Files:**
- Create: `src/DistSharp.Core/Abstractions/ILlmProvider.cs`
- Create: `src/DistSharp.Core/Abstractions/ILlmProviderFactory.cs`
- Create: `src/DistSharp.Core/Abstractions/ISolutionAnalyzer.cs`
- Create: `src/DistSharp.Core/Abstractions/IDatasetWriter.cs`
- Create: `src/DistSharp.Core/Abstractions/IDatasetWriterFactory.cs`
- Create: `src/DistSharp.Core/Abstractions/ICheckpointStore.cs`
- Create: `src/DistSharp.Core/Pipeline/IStep.cs`
- Create: `src/DistSharp.Core/Pipeline/IPipelineExecutor.cs`
- Create: `src/DistSharp.Core/Pipeline/StepDefinition.cs`
- Create: `src/DistSharp.Core/Pipeline/PipelineDefinition.cs`

Interfaces have no behavior, so no unit tests. The solution must build successfully.

- [ ] **Step 1: Create LLM provider interfaces**

`src/DistSharp.Core/Abstractions/ILlmProvider.cs`:
```csharp
using DistSharp.Core.Models;

namespace DistSharp.Core.Abstractions;

/// <summary>Abstraction over an LLM API. Implemented in <c>DistSharp.Providers</c>.</summary>
public interface ILlmProvider
{
    /// <summary>Gets the provider name, e.g. <c>openai</c>, <c>anthropic</c>.</summary>
    string ProviderName { get; }

    /// <summary>Sends <paramref name="messages"/> to the model and returns the completion text.</summary>
    /// <param name="messages">The conversation turns to send.</param>
    /// <param name="options">Per-request options such as model, temperature, and max tokens.</param>
    /// <param name="cancellationToken">Token to cancel the request.</param>
    /// <returns>The model's completion text.</returns>
    Task<string> CompleteAsync(
        IReadOnlyList<ChatMessage> messages,
        LlmRequestOptions options,
        CancellationToken cancellationToken);
}
```

`src/DistSharp.Core/Abstractions/ILlmProviderFactory.cs`:
```csharp
namespace DistSharp.Core.Abstractions;

/// <summary>Creates <see cref="ILlmProvider"/> instances from configuration.</summary>
public interface ILlmProviderFactory
{
    /// <summary>Returns the <see cref="ILlmProvider"/> for the given <paramref name="providerName"/>.</summary>
    /// <param name="providerName">Provider identifier, e.g. <c>openai</c>, <c>anthropic</c>.</param>
    /// <returns>The configured provider.</returns>
    /// <exception cref="InvalidOperationException">Thrown when <paramref name="providerName"/> is not registered.</exception>
    ILlmProvider Create(string providerName);
}
```

- [ ] **Step 2: Create solution analyzer and dataset writer interfaces**

`src/DistSharp.Core/Abstractions/ISolutionAnalyzer.cs`:
```csharp
using DistSharp.Core.Models;

namespace DistSharp.Core.Abstractions;

/// <summary>Analyses a .NET solution and yields extracted symbols. Implemented in <c>DistSharp.Roslyn</c>.</summary>
public interface ISolutionAnalyzer
{
    /// <summary>Analyses the solution at <paramref name="solutionPath"/> and streams the extracted symbols.</summary>
    /// <param name="solutionPath">Path to a <c>.sln</c>, <c>.csproj</c>, or directory.</param>
    /// <param name="options">Analysis options such as namespace exclusions and complexity threshold.</param>
    /// <param name="cancellationToken">Token to cancel the analysis.</param>
    /// <returns>An async stream of <see cref="ExtractedSymbol"/> records.</returns>
    IAsyncEnumerable<ExtractedSymbol> AnalyzeAsync(
        string solutionPath,
        SolutionAnalysisOptions options,
        CancellationToken cancellationToken);
}
```

`src/DistSharp.Core/Abstractions/IDatasetWriter.cs`:
```csharp
using DistSharp.Core.Models;

namespace DistSharp.Core.Abstractions;

/// <summary>Incrementally writes rows to a dataset output (JSONL, Parquet, CSV, etc.).</summary>
public interface IDatasetWriter : IAsyncDisposable
{
    /// <summary>Writes a single <paramref name="row"/> to the output.</summary>
    /// <param name="row">The row to write.</param>
    /// <param name="cancellationToken">Token to cancel the write.</param>
    Task WriteAsync(Row row, CancellationToken cancellationToken);

    /// <summary>Flushes any buffered rows to the underlying storage.</summary>
    /// <param name="cancellationToken">Token to cancel the flush.</param>
    Task FlushAsync(CancellationToken cancellationToken);
}
```

`src/DistSharp.Core/Abstractions/IDatasetWriterFactory.cs`:
```csharp
using DistSharp.Core.Configuration;

namespace DistSharp.Core.Abstractions;

/// <summary>Creates <see cref="IDatasetWriter"/> instances from output configuration.</summary>
public interface IDatasetWriterFactory
{
    /// <summary>Creates a writer for the format and path specified in <paramref name="config"/>.</summary>
    /// <param name="config">Output format and destination configuration.</param>
    /// <returns>A ready-to-use dataset writer.</returns>
    IDatasetWriter Create(OutputConfig config);
}
```

`src/DistSharp.Core/Abstractions/ICheckpointStore.cs`:
```csharp
using DistSharp.Core.Models;

namespace DistSharp.Core.Abstractions;

/// <summary>Persists and retrieves pipeline checkpoints to support resumable runs.</summary>
public interface ICheckpointStore
{
    /// <summary>Loads the most recent checkpoint for <paramref name="pipelineId"/>, or <see langword="null"/> if none exists.</summary>
    /// <param name="pipelineId">The stable pipeline identifier.</param>
    /// <param name="cancellationToken">Token to cancel the load.</param>
    /// <returns>The checkpoint, or <see langword="null"/>.</returns>
    Task<PipelineCheckpoint?> LoadAsync(string pipelineId, CancellationToken cancellationToken);

    /// <summary>Saves <paramref name="checkpoint"/>, overwriting any previous checkpoint for the same pipeline.</summary>
    /// <param name="checkpoint">The checkpoint to persist.</param>
    /// <param name="cancellationToken">Token to cancel the save.</param>
    Task SaveAsync(PipelineCheckpoint checkpoint, CancellationToken cancellationToken);
}
```

- [ ] **Step 3: Create pipeline types**

`src/DistSharp.Core/Pipeline/IStep.cs`:
```csharp
using System.Threading.Channels;
using DistSharp.Core.Models;

namespace DistSharp.Core.Pipeline;

/// <summary>
/// A single unit of work in a pipeline. Reads rows from <paramref name="input"/>, transforms or
/// filters them, and writes results to <paramref name="output"/>. Channel wiring is handled by
/// <see cref="IPipelineExecutor"/> — steps never create channels.
/// </summary>
public interface IStep
{
    /// <summary>Gets the unique name of this step within a pipeline.</summary>
    string Name { get; }

    /// <summary>Processes rows from <paramref name="input"/> and writes results to <paramref name="output"/>.</summary>
    /// <param name="input">The channel to read rows from. Completes when the upstream step is finished.</param>
    /// <param name="output">The channel to write result rows to.</param>
    /// <param name="cancellationToken">Token to cancel the step.</param>
    Task ExecuteAsync(
        ChannelReader<Row> input,
        ChannelWriter<Row> output,
        CancellationToken cancellationToken);
}
```

`src/DistSharp.Core/Pipeline/StepDefinition.cs`:
```csharp
namespace DistSharp.Core.Pipeline;

/// <summary>Describes a step's position in a <see cref="PipelineDefinition"/> DAG.</summary>
public sealed class StepDefinition
{
    /// <summary>Gets the unique step name (matches <see cref="IStep.Name"/>).</summary>
    public string Name { get; }

    /// <summary>Gets the step implementation to run.</summary>
    public IStep Step { get; }

    /// <summary>Gets the names of steps whose output this step depends on. Empty for source steps.</summary>
    public IReadOnlyList<string> DependsOn { get; }

    /// <summary>Initialises a new <see cref="StepDefinition"/>.</summary>
    /// <param name="name">The step name.</param>
    /// <param name="step">The step implementation.</param>
    /// <param name="dependsOn">Upstream step names. Pass <see langword="null"/> or empty for source steps.</param>
    public StepDefinition(string name, IStep step, IReadOnlyList<string>? dependsOn = null)
    {
        Name = name;
        Step = step;
        DependsOn = dependsOn ?? [];
    }
}
```

`src/DistSharp.Core/Pipeline/IPipelineExecutor.cs`:
```csharp
using DistSharp.Core.Abstractions;

namespace DistSharp.Core.Pipeline;

/// <summary>Executes a <see cref="PipelineDefinition"/>, routing rows through steps and into a dataset writer.</summary>
public interface IPipelineExecutor
{
    /// <summary>Runs all steps in <paramref name="pipeline"/> and writes output rows to <paramref name="writer"/>.</summary>
    /// <param name="pipeline">The pipeline to execute.</param>
    /// <param name="writer">The sink that receives completed rows from the final step(s).</param>
    /// <param name="cancellationToken">Token to cancel the run.</param>
    Task ExecuteAsync(
        PipelineDefinition pipeline,
        IDatasetWriter writer,
        CancellationToken cancellationToken);
}
```

`src/DistSharp.Core/Pipeline/PipelineDefinition.cs`:
```csharp
namespace DistSharp.Core.Pipeline;

/// <summary>The validated, instantiated form of a pipeline: a named DAG of <see cref="StepDefinition"/> nodes.</summary>
public sealed class PipelineDefinition
{
    /// <summary>Gets the pipeline name (used in logs and checkpoint IDs).</summary>
    public string Name { get; }

    /// <summary>Gets the ordered list of step definitions. Order does not imply execution order — the executor topologically sorts them.</summary>
    public IReadOnlyList<StepDefinition> Steps { get; }

    /// <summary>Initialises a new <see cref="PipelineDefinition"/>.</summary>
    /// <param name="name">The pipeline name.</param>
    /// <param name="steps">The step definitions.</param>
    public PipelineDefinition(string name, IReadOnlyList<StepDefinition> steps)
    {
        Name = name;
        Steps = steps;
    }
}
```

- [ ] **Step 4: Verify the solution builds**

```bash
dotnet build
```

Expected: `Build succeeded.`

- [ ] **Step 5: Commit**

```bash
git add src/DistSharp.Core/Abstractions/ src/DistSharp.Core/Pipeline/
git commit -m "feat: define core interfaces (ILlmProvider, ISolutionAnalyzer, IDatasetWriter, ICheckpointStore) and pipeline types"
```

---

## Task 6: Implement configuration model

**Files:**
- Create: `src/DistSharp.Core/Configuration/PipelineConfig.cs`
- Create: `src/DistSharp.Core/Configuration/SolutionConfig.cs`
- Create: `src/DistSharp.Core/Configuration/StepConfig.cs`
- Create: `src/DistSharp.Core/Configuration/OutputConfig.cs`

- [ ] **Step 1: Create configuration classes**

`src/DistSharp.Core/Configuration/SolutionConfig.cs`:
```csharp
namespace DistSharp.Core.Configuration;

/// <summary>Solution analysis settings bound from the <c>solution</c> section of <c>distsharp.yaml</c>.</summary>
public sealed class SolutionConfig
{
    /// <summary>Gets or sets the path to the <c>.sln</c>, <c>.csproj</c>, or directory to analyse.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether test projects are included. Default: <see langword="false"/>.</summary>
    public bool IncludeTests { get; set; }

    /// <summary>Gets or sets a value indicating whether auto-generated files are included. Default: <see langword="false"/>.</summary>
    public bool IncludeGenerated { get; set; }

    /// <summary>Gets or sets the minimum cyclomatic complexity. Default: 3.</summary>
    public int MinComplexity { get; set; } = 3;

    /// <summary>Gets or sets namespace prefixes to exclude.</summary>
    public List<string> ExcludeNamespaces { get; set; } = [];
}
```

`src/DistSharp.Core/Configuration/StepConfig.cs`:
```csharp
namespace DistSharp.Core.Configuration;

/// <summary>Configuration for a single step in a pipeline, bound from a <c>steps[]</c> entry in <c>distsharp.yaml</c>.</summary>
public sealed class StepConfig
{
    /// <summary>Gets or sets the unique step name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the step type identifier (e.g. <c>RoslynSymbolExtractor</c>, <c>LlmStep</c>).</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>Gets or sets the names of upstream steps this step depends on.</summary>
    public List<string> DependsOn { get; set; } = [];

    /// <summary>Gets or sets arbitrary step-specific configuration values.</summary>
    public Dictionary<string, object?> Config { get; set; } = [];
}
```

`src/DistSharp.Core/Configuration/OutputConfig.cs`:
```csharp
namespace DistSharp.Core.Configuration;

/// <summary>Output format and destination settings bound from the <c>output</c> section of <c>distsharp.yaml</c>.</summary>
public sealed class OutputConfig
{
    /// <summary>Gets or sets the output directory. Default: <c>./distsharp-out</c>.</summary>
    public string Dir { get; set; } = "./distsharp-out";

    /// <summary>Gets or sets the output format: <c>jsonl</c>, <c>parquet</c>, or <c>csv</c>. Default: <c>jsonl</c>.</summary>
    public string Format { get; set; } = "jsonl";

    /// <summary>Gets or sets how many rows to write between checkpoint saves. Default: 1000.</summary>
    public int CheckpointEvery { get; set; } = 1000;

    /// <summary>Gets or sets a value indicating whether symbol metadata is included as extra fields. Default: <see langword="true"/>.</summary>
    public bool WriteMetadata { get; set; } = true;
}
```

`src/DistSharp.Core/Configuration/PipelineConfig.cs`:
```csharp
namespace DistSharp.Core.Configuration;

/// <summary>
/// Strongly-typed representation of <c>distsharp.yaml</c>. Bound via
/// <c>IOptions&lt;PipelineConfig&gt;</c> — all sources (YAML file, environment variables, CLI flags)
/// are merged before binding.
/// </summary>
public sealed class PipelineConfig
{
    /// <summary>Gets or sets the pipeline name used in output metadata and checkpoint IDs.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the config schema version.</summary>
    public string Version { get; set; } = "1";

    /// <summary>Gets or sets the solution analysis settings.</summary>
    public SolutionConfig Solution { get; set; } = new();

    /// <summary>Gets or sets the ordered list of step configurations.</summary>
    public List<StepConfig> Steps { get; set; } = [];

    /// <summary>Gets or sets the output format and destination settings.</summary>
    public OutputConfig Output { get; set; } = new();
}
```

- [ ] **Step 2: Verify the solution builds**

```bash
dotnet build
```

Expected: `Build succeeded.`

- [ ] **Step 3: Commit**

```bash
git add src/DistSharp.Core/Configuration/
git commit -m "feat: add PipelineConfig, SolutionConfig, StepConfig, OutputConfig"
```

---

## Task 7: Implement `FileCheckpointStore` (TDD)

**Files:**
- Create: `src/DistSharp.Core/Checkpointing/FileCheckpointStore.cs`
- Create: `tests/DistSharp.Core.Tests/Checkpointing/FileCheckpointStoreTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `tests/DistSharp.Core.Tests/Checkpointing/FileCheckpointStoreTests.cs`:

```csharp
using DistSharp.Core.Checkpointing;
using DistSharp.Core.Models;
using FluentAssertions;

namespace DistSharp.Core.Tests.Checkpointing;

public sealed class FileCheckpointStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public async Task LoadAsync_ReturnsNull_WhenNoCheckpointExists()
    {
        var store = new FileCheckpointStore(_dir);

        var result = await store.LoadAsync("run-1", CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task SaveThenLoad_RoundTrips_AllFields()
    {
        var store = new FileCheckpointStore(_dir);
        var saved = new PipelineCheckpoint("run-1", "step-extract", 5000L, DateTimeOffset.UtcNow);

        await store.SaveAsync(saved, CancellationToken.None);
        var loaded = await store.LoadAsync("run-1", CancellationToken.None);

        loaded.Should().NotBeNull();
        loaded!.PipelineId.Should().Be("run-1");
        loaded.StepName.Should().Be("step-extract");
        loaded.RowsWritten.Should().Be(5000L);
        loaded.SavedAt.Should().BeCloseTo(saved.SavedAt, TimeSpan.FromMilliseconds(100));
    }

    [Fact]
    public async Task SaveAsync_Overwrites_PreviousCheckpoint()
    {
        var store = new FileCheckpointStore(_dir);
        var first = new PipelineCheckpoint("run-1", "step-a", 100L, DateTimeOffset.UtcNow);
        var second = new PipelineCheckpoint("run-1", "step-b", 200L, DateTimeOffset.UtcNow);

        await store.SaveAsync(first, CancellationToken.None);
        await store.SaveAsync(second, CancellationToken.None);
        var loaded = await store.LoadAsync("run-1", CancellationToken.None);

        loaded!.RowsWritten.Should().Be(200L);
        loaded.StepName.Should().Be("step-b");
    }

    [Fact]
    public async Task LoadAsync_ReturnsNull_ForUnknownPipelineId()
    {
        var store = new FileCheckpointStore(_dir);
        await store.SaveAsync(new PipelineCheckpoint("run-1", "step-a", 100L, DateTimeOffset.UtcNow), CancellationToken.None);

        var result = await store.LoadAsync("run-999", CancellationToken.None);

        result.Should().BeNull();
    }
}
```

- [ ] **Step 2: Run to verify the tests fail**

```bash
dotnet test tests/DistSharp.Core.Tests --filter "FullyQualifiedName~FileCheckpointStoreTests"
```

Expected: build error — `FileCheckpointStore` does not exist yet.

- [ ] **Step 3: Implement `FileCheckpointStore`**

Create `src/DistSharp.Core/Checkpointing/FileCheckpointStore.cs`:

```csharp
using System.Text.Json;
using DistSharp.Core.Abstractions;
using DistSharp.Core.Models;

namespace DistSharp.Core.Checkpointing;

/// <summary>
/// Persists pipeline checkpoints as JSON files under <c>&lt;baseDir&gt;/&lt;pipelineId&gt;.checkpoint.json</c>.
/// </summary>
public sealed class FileCheckpointStore : ICheckpointStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly string _baseDir;

    /// <summary>Initialises a new <see cref="FileCheckpointStore"/> that writes to <paramref name="baseDir"/>.</summary>
    /// <param name="baseDir">The directory in which checkpoint files are stored. Created on first write.</param>
    public FileCheckpointStore(string baseDir) => _baseDir = baseDir;

    /// <inheritdoc/>
    public async Task<PipelineCheckpoint?> LoadAsync(string pipelineId, CancellationToken cancellationToken)
    {
        var path = FilePath(pipelineId);
        if (!File.Exists(path))
            return null;

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<PipelineCheckpoint>(stream, JsonOptions, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task SaveAsync(PipelineCheckpoint checkpoint, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_baseDir);
        var path = FilePath(checkpoint.PipelineId);

        await using var stream = File.Open(path, FileMode.Create, FileAccess.Write, FileShare.None);
        await JsonSerializer.SerializeAsync(stream, checkpoint, JsonOptions, cancellationToken);
    }

    private string FilePath(string pipelineId) =>
        Path.Combine(_baseDir, $"{pipelineId}.checkpoint.json");
}
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
dotnet test tests/DistSharp.Core.Tests --filter "FullyQualifiedName~FileCheckpointStoreTests"
```

Expected: `4 passed`.

- [ ] **Step 5: Commit**

```bash
git add src/DistSharp.Core/Checkpointing/FileCheckpointStore.cs tests/DistSharp.Core.Tests/Checkpointing/FileCheckpointStoreTests.cs
git commit -m "feat: implement FileCheckpointStore — JSON-backed checkpoint persistence"
```

---

## Task 8: Implement `PipelineExecutor` — linear pipeline (TDD)

**Files:**
- Create: `src/DistSharp.Core/Pipeline/PipelineExecutor.cs`
- Create: `tests/DistSharp.Core.Tests/Pipeline/PipelineExecutorTests.cs`

- [ ] **Step 1: Write the failing linear-pipeline tests**

Create `tests/DistSharp.Core.Tests/Pipeline/PipelineExecutorTests.cs`:

```csharp
using System.Threading.Channels;
using DistSharp.Core.Abstractions;
using DistSharp.Core.Models;
using DistSharp.Core.Pipeline;
using FluentAssertions;

namespace DistSharp.Core.Tests.Pipeline;

public sealed class PipelineExecutorTests
{
    // ── Test helpers ──────────────────────────────────────────────────────────

    // Produces a fixed set of rows and ignores its input (source step behaviour).
    private sealed class SourceStep(string name, params Row[] rows) : IStep
    {
        public string Name { get; } = name;

        public async Task ExecuteAsync(ChannelReader<Row> input, ChannelWriter<Row> output, CancellationToken ct)
        {
            foreach (var row in rows)
                await output.WriteAsync(row, ct);
        }
    }

    // Passes every row through unchanged.
    private sealed class PassthroughStep(string name) : IStep
    {
        public string Name { get; } = name;

        public async Task ExecuteAsync(ChannelReader<Row> input, ChannelWriter<Row> output, CancellationToken ct)
        {
            await foreach (var row in input.ReadAllAsync(ct))
                await output.WriteAsync(row, ct);
        }
    }

    // Appends a field to every row it receives.
    private sealed class TagStep(string name, string field, string tag) : IStep
    {
        public string Name { get; } = name;

        public async Task ExecuteAsync(ChannelReader<Row> input, ChannelWriter<Row> output, CancellationToken ct)
        {
            await foreach (var row in input.ReadAllAsync(ct))
                await output.WriteAsync(row.With(field, tag), ct);
        }
    }

    // Collects every row written to it.
    private sealed class CollectingWriter : IDatasetWriter
    {
        public List<Row> Rows { get; } = [];

        public Task WriteAsync(Row row, CancellationToken ct) { Rows.Add(row); return Task.CompletedTask; }
        public Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    // ── Linear pipeline tests ─────────────────────────────────────────────────

    [Fact]
    public async Task Linear_SourceOnly_WritesAllRowsToDatasetWriter()
    {
        var row1 = Row.Empty.With("id", 1);
        var row2 = Row.Empty.With("id", 2);
        var source = new SourceStep("source", row1, row2);
        var pipeline = new PipelineDefinition("test", [new StepDefinition("source", source)]);
        var writer = new CollectingWriter();

        await new PipelineExecutor().ExecuteAsync(pipeline, writer, CancellationToken.None);

        writer.Rows.Should().HaveCount(2);
        writer.Rows[0].Get<int>("id").Should().Be(1);
        writer.Rows[1].Get<int>("id").Should().Be(2);
    }

    [Fact]
    public async Task Linear_ThreeSteps_TransformationsAppliedInOrder()
    {
        var source = new SourceStep("source", Row.Empty.With("val", "original"));
        var tag1 = new TagStep("tag1", "step1", "done");
        var tag2 = new TagStep("tag2", "step2", "done");

        var pipeline = new PipelineDefinition("test",
        [
            new StepDefinition("source", source),
            new StepDefinition("tag1", tag1, ["source"]),
            new StepDefinition("tag2", tag2, ["tag1"]),
        ]);
        var writer = new CollectingWriter();

        await new PipelineExecutor().ExecuteAsync(pipeline, writer, CancellationToken.None);

        writer.Rows.Should().HaveCount(1);
        writer.Rows[0].Get<string>("val").Should().Be("original");
        writer.Rows[0].Get<string>("step1").Should().Be("done");
        writer.Rows[0].Get<string>("step2").Should().Be("done");
    }

    [Fact]
    public async Task Linear_EmptySource_WritesNoRows()
    {
        var source = new SourceStep("source"); // no rows
        var pipeline = new PipelineDefinition("test", [new StepDefinition("source", source)]);
        var writer = new CollectingWriter();

        await new PipelineExecutor().ExecuteAsync(pipeline, writer, CancellationToken.None);

        writer.Rows.Should().BeEmpty();
    }
}
```

- [ ] **Step 2: Run to verify the tests fail**

```bash
dotnet test tests/DistSharp.Core.Tests --filter "FullyQualifiedName~PipelineExecutorTests"
```

Expected: build error — `PipelineExecutor` does not exist yet.

- [ ] **Step 3: Implement `PipelineExecutor` (linear only)**

Create `src/DistSharp.Core/Pipeline/PipelineExecutor.cs`:

```csharp
using System.Threading.Channels;
using DistSharp.Core.Abstractions;
using DistSharp.Core.Models;

namespace DistSharp.Core.Pipeline;

/// <summary>
/// Executes a <see cref="PipelineDefinition"/> by wiring steps together via
/// <see cref="System.Threading.Channels"/>. Supports linear pipelines, fan-out
/// (one step → many), and fan-in (many steps → one).
/// </summary>
public sealed class PipelineExecutor : IPipelineExecutor
{
    private const int ChannelCapacity = 1000;

    /// <inheritdoc/>
    public async Task ExecuteAsync(PipelineDefinition pipeline, IDatasetWriter writer, CancellationToken cancellationToken)
    {
        var steps = TopologicalSort(pipeline.Steps);
        var count = steps.Count;

        // stepIndex[name] = index into the steps array
        var stepIndex = steps.Select((s, i) => (s.Name, i)).ToDictionary(x => x.Name, x => x.i);

        // consumers[i] = indices of steps that list steps[i] as a dependency
        var consumers = Enumerable.Range(0, count)
            .Select(i => steps
                .Select((s, j) => (s, j))
                .Where(x => x.s.DependsOn.Contains(steps[i].Name))
                .Select(x => x.j)
                .ToArray())
            .ToArray();

        // Each step writes to its own output channel.
        var outputChannels = Enumerable.Range(0, count)
            .Select(_ => Channel.CreateBounded<Row>(ChannelCapacity))
            .ToArray();

        // Each step reads from its own input channel.
        // Source steps (no DependsOn) get a pre-completed empty channel.
        // Fan-in steps (multiple DependsOn) need SingleWriter=false.
        var inputChannels = steps
            .Select(s =>
            {
                if (s.DependsOn.Count == 0)
                {
                    var empty = Channel.CreateBounded<Row>(1);
                    empty.Writer.Complete();
                    return empty;
                }

                return Channel.CreateBounded<Row>(new BoundedChannelOptions(ChannelCapacity)
                {
                    SingleWriter = s.DependsOn.Count == 1,
                });
            })
            .ToArray();

        // fanInCounters[i] = number of upstream distributor tasks still writing to inputChannels[i].
        // When this reaches 0, the distributor completing it calls TryComplete on inputChannels[i].Writer.
        var fanInCounters = steps.Select(s => s.DependsOn.Count).ToArray();

        // Step tasks: run each step, then complete its output channel.
        var stepTasks = steps.Select((s, i) =>
            s.Step.ExecuteAsync(inputChannels[i].Reader, outputChannels[i].Writer, cancellationToken)
                .ContinueWith(
                    t => outputChannels[i].Writer.TryComplete(t.IsFaulted ? t.Exception : null),
                    CancellationToken.None,
                    TaskContinuationOptions.None,
                    TaskScheduler.Default))
            .ToArray();

        // Distributor tasks: for each step, read its output channel and forward to consumers (or the writer).
        var distributorTasks = steps.Select((_, i) =>
            consumers[i].Length == 0
                ? DrainToWriterAsync(outputChannels[i].Reader, writer, cancellationToken)
                : DistributeAsync(outputChannels[i].Reader, consumers[i], inputChannels, fanInCounters, cancellationToken))
            .ToArray();

        await Task.WhenAll(stepTasks.Concat(distributorTasks));
    }

    // Reads every row from source and forwards a copy to each consumer's input channel.
    // When source is exhausted, decrements the fan-in counter for each consumer and completes
    // their input channel if the counter reaches zero (i.e. all upstreams are done).
    private static async Task DistributeAsync(
        ChannelReader<Row> source,
        int[] consumerIndices,
        Channel<Row>[] inputChannels,
        int[] fanInCounters,
        CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var row in source.ReadAllAsync(cancellationToken))
                foreach (var idx in consumerIndices)
                    await inputChannels[idx].Writer.WriteAsync(row, cancellationToken);
        }
        finally
        {
            foreach (var idx in consumerIndices)
                if (Interlocked.Decrement(ref fanInCounters[idx]) == 0)
                    inputChannels[idx].Writer.TryComplete();
        }
    }

    private static async Task DrainToWriterAsync(
        ChannelReader<Row> source,
        IDatasetWriter writer,
        CancellationToken cancellationToken)
    {
        await foreach (var row in source.ReadAllAsync(cancellationToken))
            await writer.WriteAsync(row, cancellationToken);
    }

    private static List<StepDefinition> TopologicalSort(IReadOnlyList<StepDefinition> steps)
    {
        var sorted = new List<StepDefinition>(steps.Count);
        var permanent = new HashSet<string>(steps.Count);
        var temporary = new HashSet<string>();
        var byName = steps.ToDictionary(s => s.Name);

        void Visit(StepDefinition step)
        {
            if (permanent.Contains(step.Name))
                return;

            if (!temporary.Add(step.Name))
                throw new InvalidOperationException($"Pipeline has a cycle involving step '{step.Name}'.");

            foreach (var dep in step.DependsOn)
                Visit(byName[dep]);

            temporary.Remove(step.Name);
            permanent.Add(step.Name);
            sorted.Add(step);
        }

        foreach (var step in steps)
            Visit(step);

        return sorted;
    }
}
```

- [ ] **Step 4: Run tests to verify linear tests pass**

```bash
dotnet test tests/DistSharp.Core.Tests --filter "FullyQualifiedName~PipelineExecutorTests"
```

Expected: `3 passed`.

- [ ] **Step 5: Commit**

```bash
git add src/DistSharp.Core/Pipeline/PipelineExecutor.cs tests/DistSharp.Core.Tests/Pipeline/PipelineExecutorTests.cs
git commit -m "feat: implement PipelineExecutor — channel-based DAG executor (linear pipelines)"
```

---

## Task 9: Extend `PipelineExecutor` for fan-out and fan-in (TDD)

**Files:**
- Modify: `tests/DistSharp.Core.Tests/Pipeline/PipelineExecutorTests.cs`
- No changes needed to `PipelineExecutor.cs` — the implementation already handles fan-out and fan-in.

The `PipelineExecutor` already handles fan-out and fan-in through the `DistributeAsync` / `fanInCounters` mechanism. This task adds tests to prove both cases work correctly.

- [ ] **Step 1: Add fan-out and fan-in tests**

Append these test methods to `PipelineExecutorTests.cs` (inside the class, after the existing tests):

```csharp
// ── Fan-out tests ─────────────────────────────────────────────────────────────

[Fact]
public async Task FanOut_OneSourceTwoConsumers_BothReceiveAllRows()
{
    var source = new SourceStep("source",
        Row.Empty.With("id", 1),
        Row.Empty.With("id", 2));
    var branchA = new TagStep("branchA", "branch", "a");
    var branchB = new TagStep("branchB", "branch", "b");

    var pipeline = new PipelineDefinition("test",
    [
        new StepDefinition("source", source),
        new StepDefinition("branchA", branchA, ["source"]),
        new StepDefinition("branchB", branchB, ["source"]),
    ]);
    var writer = new CollectingWriter();

    await new PipelineExecutor().ExecuteAsync(pipeline, writer, CancellationToken.None);

    // Both branches receive 2 rows each = 4 rows total
    writer.Rows.Should().HaveCount(4);
    writer.Rows.Where(r => r.Get<string>("branch") == "a").Should().HaveCount(2);
    writer.Rows.Where(r => r.Get<string>("branch") == "b").Should().HaveCount(2);
}

// ── Fan-in tests ──────────────────────────────────────────────────────────────

[Fact]
public async Task FanIn_TwoUpstreamsMerge_DownstreamReceivesAllRows()
{
    var sourceA = new SourceStep("sourceA", Row.Empty.With("src", "a"));
    var sourceB = new SourceStep("sourceB", Row.Empty.With("src", "b"));
    var merge = new PassthroughStep("merge");

    var pipeline = new PipelineDefinition("test",
    [
        new StepDefinition("sourceA", sourceA),
        new StepDefinition("sourceB", sourceB),
        new StepDefinition("merge", merge, ["sourceA", "sourceB"]),
    ]);
    var writer = new CollectingWriter();

    await new PipelineExecutor().ExecuteAsync(pipeline, writer, CancellationToken.None);

    writer.Rows.Should().HaveCount(2);
    writer.Rows.Select(r => r.Get<string>("src")).Should().BeEquivalentTo(["a", "b"]);
}

[Fact]
public async Task FanOutThenFanIn_DiamondTopology_AllRowsReachSink()
{
    // source → branchA ─┐
    //                    ├→ merge → sink
    // source → branchB ─┘
    var source = new SourceStep("source",
        Row.Empty.With("id", 1),
        Row.Empty.With("id", 2));
    var branchA = new TagStep("branchA", "branch", "a");
    var branchB = new TagStep("branchB", "branch", "b");
    var merge = new PassthroughStep("merge");

    var pipeline = new PipelineDefinition("test",
    [
        new StepDefinition("source", source),
        new StepDefinition("branchA", branchA, ["source"]),
        new StepDefinition("branchB", branchB, ["source"]),
        new StepDefinition("merge", merge, ["branchA", "branchB"]),
    ]);
    var writer = new CollectingWriter();

    await new PipelineExecutor().ExecuteAsync(pipeline, writer, CancellationToken.None);

    // 2 source rows × 2 branches = 4 merged rows
    writer.Rows.Should().HaveCount(4);
    writer.Rows.Select(r => r.Get<int>("id")).Should().BeEquivalentTo([1, 2, 1, 2]);
}

// ── Cycle detection ───────────────────────────────────────────────────────────

[Fact]
public async Task Cycle_ThrowsInvalidOperationException()
{
    var stepA = new PassthroughStep("a");
    var stepB = new PassthroughStep("b");

    var pipeline = new PipelineDefinition("test",
    [
        new StepDefinition("a", stepA, ["b"]),
        new StepDefinition("b", stepB, ["a"]),
    ]);
    var writer = new CollectingWriter();

    var act = () => new PipelineExecutor().ExecuteAsync(pipeline, writer, CancellationToken.None);

    await act.Should().ThrowAsync<InvalidOperationException>()
        .WithMessage("*cycle*");
}
```

- [ ] **Step 2: Run all PipelineExecutor tests**

```bash
dotnet test tests/DistSharp.Core.Tests --filter "FullyQualifiedName~PipelineExecutorTests"
```

Expected: `7 passed`.

- [ ] **Step 3: Commit**

```bash
git add tests/DistSharp.Core.Tests/Pipeline/PipelineExecutorTests.cs
git commit -m "test: add fan-out, fan-in, diamond, and cycle-detection tests for PipelineExecutor"
```

---

## Task 10: Final verification

- [ ] **Step 1: Run the full test suite**

```bash
dotnet test
```

Expected: all tests pass, 0 failures.

- [ ] **Step 2: Build in Release mode**

```bash
dotnet build --configuration Release
```

Expected: `Build succeeded.`

- [ ] **Step 3: Commit**

If there are any uncommitted files (unlikely at this point):

```bash
git status
git add -A
git commit -m "feat: complete architecture phase — solution scaffold, core abstractions, pipeline executor, file checkpoint store"
```

---

## Self-Review Notes

**Spec coverage check:**
- ✅ Solution structure (Tasks 1–2)
- ✅ `Row` with immutability, `With`, `Get<T>`, `TryGet<T>` (Task 3)
- ✅ `ChatMessage`, `ExtractedSymbol`, `LlmRequestOptions`, `SolutionAnalysisOptions`, `PipelineCheckpoint` (Task 4)
- ✅ `ILlmProvider`, `ILlmProviderFactory`, `ISolutionAnalyzer`, `IDatasetWriter`, `IDatasetWriterFactory`, `ICheckpointStore` (Task 5)
- ✅ `IStep`, `IPipelineExecutor`, `StepDefinition`, `PipelineDefinition` (Task 5)
- ✅ `PipelineConfig`, `SolutionConfig`, `StepConfig`, `OutputConfig` (Task 6)
- ✅ `FileCheckpointStore` (Task 7)
- ✅ `PipelineExecutor` — linear, fan-out, fan-in, cycle detection (Tasks 8–9)
- ✅ Central Package Management, StyleCop, .editorconfig, CI (Tasks 1–2)
- ✅ Build sequence phases 1–2 fully covered

**Type consistency check:**
- `Row` uses `ImmutableDictionary<string, object?>` internally; `Fields` property is `IReadOnlyDictionary<string, object?>` — consistent throughout all tasks ✅
- `IDatasetWriterFactory.Create(OutputConfig)` — `OutputConfig` defined in Task 6, referenced in Task 5 (interface definition order). The build will succeed because both are in the same project and compiled together ✅
- `PipelineExecutor` constructor has no parameters (Tasks 8–9 call `new PipelineExecutor()`) — consistent ✅
- `StepDefinition` constructor signature: `(string name, IStep step, IReadOnlyList<string>? dependsOn = null)` — used consistently in all tests ✅

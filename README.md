# Universal Vector DB Connector

**One set of actions for vector databases**, built as an
[OutSystems Developer Cloud (ODC)](https://www.outsystems.com/developer-cloud/) external library.
It also works as a plain .NET 10 library.

> **Status: Preview (0.1.0).** The only provider implemented so far is **Qdrant** (Qdrant Cloud or self-hosted).
> Pinecone, pgvector, Azure AI Search, Weaviate, Milvus/Zilliz, Elasticsearch, MongoDB Atlas Vector Search and Redis are planned.

## Why

ODC's existing vector-DB assets each target a single vendor. This library gives ODC apps one action set
(collections, upsert, filtered similarity search, fetch, count, delete), so moving to another provider
means changing configuration, not code. See [docs/architecture.md](docs/architecture.md) for the design.

## Features

- **14 actions:** `TestConnection`, `GetCapabilities`, `CreateCollection`, `DeleteCollection`, `ListCollections`,
  `CollectionExists`, `Upsert`, `Query`, `Fetch`, `Delete`, `DeleteByFilter`, `Count`, `EncodeVector`, `DecodeVector`
- **Portable filter language**, translated for each provider:
  `{"category":"invoice","year":{"$gte":2024}}`, with `$eq $ne $gt $gte $lt $lte $in $nin $exists $and $or $not`
- **Errors as data:** every action returns a `VdbStatus` (`Success`, `ErrorCode` such as `Unauthorized`,
  `NotFound`, `AlreadyExists` or `RateLimited`, `ErrorMessage`, `ProviderHttpStatus`, `RetryAfterMs`)
- **Built for ODC's limits** (95 s per call, 5.5 MB payload): a per-call time budget that covers retries,
  and `VectorBase64`, which sends vectors about 5× smaller than lists of decimals
- **Namespaces**, **automatic ID mapping** (any text can be an ID; Qdrant gets a stable UUIDv5),
  **score normalization** (`Similarity` from 0 to 1), and **API keys never logged**

## Use it in OutSystems (ODC)

1. Install **Universal Vector DB Connector** from Forge, or build the ZIP yourself (see [Build](#build)) and
   upload it in ODC Portal → External logic. Then publish it **and release it**.
2. In your app, add the dependency and two Settings: `QdrantEndpoint` and `QdrantApiKey` (mark the key **secret**).
3. From a **server action**, build a `VdbConnection` (Provider `"qdrant"`, Endpoint, ApiKey) and call the actions.
   Always check `Status.Success`.

## Use it as a .NET library

The project targets **.NET 10** and has no ODC runtime dependency. The OutSystems SDK package only
contributes attributes. Reference the project (a NuGet package is planned):

```bash
dotnet add reference path/to/src/UniversalVectorDBConnector/UniversalVectorDBConnector.csproj
```

```csharp
using UniversalVectorDBConnector;

var vdb  = new VectorDBConnector();
var conn = new VdbConnection
{
    Provider = "qdrant",
    Endpoint = "https://YOUR-CLUSTER.cloud.qdrant.io:6333",
    ApiKey   = Environment.GetEnvironmentVariable("QDRANT_API_KEY") ?? "",
};

vdb.CreateCollection(conn, new VdbCollectionSpec
{
    Name = "docs", Dimension = 3, Metric = "cosine",
    MetadataIndexesJson = "{\"category\":\"keyword\"}",
}, out var status);

vdb.Upsert(conn, "docs", new[]
{
    new VdbRecord { Id = "doc-1", Vector = new[] { 0.9m, 0.1m, 0.0m }, Text = "Invoice guide",
                    MetadataJson = "{\"category\":\"invoice\"}" },
}, out status, out var upserted);

vdb.Query(conn, new VdbQuery
{
    Collection = "docs", Vector = new[] { 1m, 0m, 0m }, TopK = 5,
    FilterJson = "{\"category\":\"invoice\"}",
}, out status, out var matches);

if (!status.Success) Console.WriteLine($"{status.ErrorCode}: {status.ErrorMessage}");
foreach (var m in matches) Console.WriteLine($"{m.Id}  {m.Similarity:P0}  {m.Text}");
```

The current API is **synchronous with `out` parameters**, because that's what ODC external libraries
require. A modern async `IVectorStore` API for .NET apps is on the roadmap. If you only need .NET,
also look at Microsoft's
[`Microsoft.Extensions.VectorData`](https://learn.microsoft.com/dotnet/ai/vector-stores/overview)
abstractions. This project's focus is ODC.

## Build

Requires the .NET 10 SDK, plus Docker for the live tests.

```bash
dotnet build UniversalVectorDBConnector.slnx
dotnet test UniversalVectorDBConnector.slnx
```

Live tests against a real Qdrant are skipped unless `QDRANT_URL` is set:

```bash
docker run -d --name uvdb-qdrant -p 6333:6333 -e QDRANT__SERVICE__API_KEY=local-test-key qdrant/qdrant
QDRANT_URL=http://localhost:6333 QDRANT_API_KEY=local-test-key dotnet test UniversalVectorDBConnector.slnx
```

**ODC upload package** (contents of the publish folder, zipped at the root):

```bash
dotnet publish src/UniversalVectorDBConnector/UniversalVectorDBConnector.csproj -c Release -r linux-x64 --self-contained false
# zip the contents of src/UniversalVectorDBConnector/bin/Release/net10.0/linux-x64/publish/
```

## Project layout

```
src/UniversalVectorDBConnector/        external library (net10.0)
  Interfaces/                          [OSInterface] — the 14 ODC actions
  Implementations/VectorDBConnector.cs dispatcher: validation, adapter choice, errors → VdbStatus
  Structures/                          [OSStructure] types (VdbConnection, VdbRecord, VdbQuery, …)
  Adapters/Qdrant/                     Qdrant REST adapter + filter translator
  Core/                                HTTP + retries/time budget, ID mapping, vector codec
src/UniversalVectorDBConnector.UnitTests/  xUnit v3: unit tests + live Qdrant tests
docs/                                  architecture and Forge listing
```

## Roadmap

- 0.2: Pinecone, pgvector, Azure AI Search; logging; optional LangChain-compatible payload layout
  (`page_content` + `metadata`), so ODC apps and LangChain/LangGraph agents can share collections
- Later: Weaviate, Milvus/Zilliz, Elasticsearch, MongoDB Atlas, Redis; hybrid and text search;
  async .NET API + NuGet package

## License

[BSD-3-Clause](LICENSE)

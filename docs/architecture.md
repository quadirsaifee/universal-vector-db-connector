# Universal Vector DB Connector (ODC) — Architecture and Action List

Status: **draft v0.1** (2026-10-20). Nothing here has been created in the tenant yet.
Owner: Abdul.

## 1. Goal

One provider-agnostic set of ODC server actions for vector databases, so an ODC app can switch
between Pinecone, Qdrant, Weaviate, Milvus/Zilliz, pgvector, MongoDB Atlas Vector Search,
Azure AI Search, Elasticsearch and Redis by changing configuration, not code.

### Why this fills a gap (research of 2026-10-20)

- ODC Forge has only single-vendor pieces: OutSystems Labs *Pinecone Connector* 0.1.0 and
  *PineconeConnectorForIntegratedEmbedding* 0.1.4 (upsert + search only), *AzureAISearch_Custom*
  (PDF only), *SemanticEngineV2 / RAG Knowledge Base* (vectors stored as text in entities),
  *MultimodalEmbeddingLibrary* (Cohere), and a Qdrant sample inside *AIAgentBuilderSourceSample*.
- Qdrant, Upstash and Xebia Pinecone connectors exist for O11 only.
- Forge has zero results for Weaviate, Milvus or pgvector.
- Native ODC: Semantic Search (Beta, own data only) and Data Fabric AI search connections
  (Azure AI Search, Amazon Kendra, custom search service).

### Non-goals (v1)

- Generating embeddings. The app brings its own vectors (from ODC AI models, the Cohere library,
  etc.). Providers with server-side embedding are supported through an optional text-query path
  (see §5.6), but the connector does not call embedding models itself.
- Chunking. Recommend the vendor-neutral *ODC Chunking Library* as the companion asset.
- Admin features beyond collections (backups, users, billing, cluster scaling).

## 2. Deliverables

| # | Asset | Type | Purpose |
|---|---|---|---|
| 1 | `UniversalVectorDBConnector` | ODC External Library (C#, .NET 10) | All provider adapters and the generic action set. The core of the Forge asset. |
| 2 | `UniversalVectorDBConnector_Client` | ODC Library (low-code) | Thin wrapper: reads connection Settings (secrets), builds the config structure, offers friendlier actions, and maps errors to ODC exceptions. Optional for users. |
| 3 | `UniversalVectorDBConnector_Demo` | ODC Web App | Playground: pick a provider, create a collection, upsert sample docs, run filtered and hybrid queries. Doubles as the live smoke test and Forge screenshots. |
| 4 | `UniversalVectorDBConnector_SearchBridge` (phase 3, to verify) | ODC App exposing a REST API | Wraps `Query` behind the contract that Data Fabric's *custom search service* expects, so ODC agents can ground on any supported vector DB. |

Forge publishes the external library and the two ODC libraries/apps together; the demo depends
on the client library, which depends on the external library.

## 3. High-level design

```
 ODC app / agent
      │  server actions
      ▼
 UniversalVectorDBConnector_Client (low-code)        ← reads Settings, builds VdbConnection
      │
      ▼
 UniversalVectorDBConnector (External Library, C#)
 ┌──────────────────────────────────────────────────────────┐
 │ IUniversalVectorDBConnector  ([OSInterface], generic actions)     │
 │        │                                                 │
 │ Dispatcher → validates input, picks adapter by Provider, │
 │              enforces time budget, retries, redacts logs │
 │        │                                                 │
 │ IVectorStoreAdapter (internal C# interface)              │
 │  ├─ PineconeAdapter      (REST)                          │
 │  ├─ QdrantAdapter        (REST)                          │
 │  ├─ WeaviateAdapter      (REST + GraphQL)                │
 │  ├─ MilvusAdapter        (REST v2; Zilliz Cloud same)    │
 │  ├─ AzureAISearchAdapter (REST)                          │
 │  ├─ ElasticAdapter       (REST)                          │
 │  ├─ PgVectorAdapter      (Npgsql, TCP)                   │
 │  ├─ MongoAtlasAdapter    (MongoDB.Driver, TCP)           │
 │  └─ RedisAdapter         (StackExchange.Redis, TCP)      │
 │        │                                                 │
 │ Shared: FilterTranslator, VectorCodec, IdMapper,         │
 │         ScoreNormalizer, HttpClient pool, error mapper   │
 └──────────────────────────────────────────────────────────┘
      │ HTTPS / TLS TCP
      ▼
 Vector DB (cloud or reachable self-hosted; private ones via ODC Private Gateway — to verify)
```

### Key decisions

1. **Single external library, generic actions with a `Provider` field.** One action set is the
   whole value proposition, and it keeps the ODC surface small (about 20 actions). Adapters are
   internal C# classes, never exposed to ODC.
2. **Stateless calls.** ODC gives no guarantee that two calls hit the same library instance, so
   every action receives the full `VdbConnection` structure. Only `HttpClient` / driver
   connection pools are cached (static, keyed by endpoint + credential hash).
3. **Prefer REST over native drivers.** Pinecone, Qdrant, Weaviate, Milvus, Azure AI Search and
   Elastic all have REST APIs, which keeps the ZIP small and avoids gRPC. Drivers only where there
   is no HTTP option: pgvector (Npgsql), MongoDB (the Atlas Data API was retired), Redis.
4. **Structured results, not exceptions.** Every action returns a `VdbStatus` (success flag,
   normalized error code, provider message, HTTP status, retry hint). The client library turns
   failures into ODC exceptions for users who prefer that. Input that can never succeed
   (missing provider, empty vector) still returns a `VdbStatus` with `InvalidInput`.
5. **Stay inside ODC limits by design** (95 s, 1 GB, 5.5 MB payload): see §6.

## 4. Data model (ODC structures)

The SDK does not support dictionaries, classes, enums or `Task`, so dynamic data uses JSON text
and enums are text with documented values.

### `VdbConnection`
| Field | Type | Notes |
|---|---|---|
| Provider | Text | `pinecone`, `qdrant`, `weaviate`, `milvus`, `pgvector`, `mongodb`, `azureaisearch`, `elastic`, `redis` |
| Endpoint | Text | Base URL, or connection string for pgvector / MongoDB / Redis |
| ApiKey | Text | Secret. Comes from an ODC Setting marked secret. Never logged. |
| Database | Text | Optional. Postgres schema, Mongo database, Milvus database, Redis DB index |
| TimeoutSeconds | Integer | Optional, default 30, capped at 85 |
| OptionsJson | Text | Optional provider extras, e.g. `{"apiVersion":"2024-07-01"}` for Azure, `{"cloud":"aws","region":"us-east-1"}` for Pinecone serverless create |

### `VdbCollectionSpec`
Name, Dimension (Integer), Metric (`cosine` / `dot` / `euclidean`), EnableSparse (Boolean, for
hybrid), MetadataIndexesJson (fields to index for filtering, with types), OptionsJson.

### `VdbFieldMapping` (bring-your-own index)
For collections not created by this connector: IdField, VectorField, SparseVectorField, TextField,
MetadataFields (list) or MetadataContainerField. Empty means the connector's own defaults
(`id`, `vector`, `sparse`, `text`, `metadata`).

### `VdbRecord`
| Field | Type | Notes |
|---|---|---|
| Id | Text | Always text in ODC; mapped per provider (see §5.4) |
| Vector | List of Decimal | Dense vector. Either this or `VectorBase64`. |
| VectorBase64 | Text | Little-endian float32 bytes, base64. About 5× smaller on the wire. |
| SparseIndices / SparseValues | List of Integer / List of Decimal | Optional, for hybrid |
| Text | Text | Optional source text (stored as a field, used for keyword / hybrid search) |
| MetadataJson | Text | Flat or nested JSON object |

### `VdbQuery`
Collection, Namespace (optional), Vector or VectorBase64, Sparse (optional), QueryText
(optional, for hybrid or server-side embedding), TopK (default 10, max 1000), FilterJson
(portable filter, §5.2), IncludeVectors (Boolean), IncludeMetadata (Boolean, default true),
MinScore (optional), HybridAlpha (0 = keyword only, 1 = vector only, default 0.5), FieldMapping.

### `VdbMatch`
Id, Score (raw provider score), Similarity (normalized 0–1 where the metric allows, §5.5), Text,
MetadataJson, Vector / VectorBase64 (when requested).

### `VdbStatus`
Success, ErrorCode (`InvalidInput`, `Unauthorized`, `NotFound`, `AlreadyExists`, `RateLimited`,
`Timeout`, `PayloadTooLarge`, `Unsupported`, `ProviderError`, `NetworkError`), ErrorMessage,
ProviderHttpStatus, RetryAfterMs, RequestId (provider's, when given).

### `VdbCapabilities`
Booleans per feature: Namespaces, HybridSearch, SparseVectors, ServerSideEmbedding,
DeleteByFilter, FetchByIds, Count, ListCollections, MetadataFilterOperators (text list),
MaxTopK, MaxBatchSize, MaxDimension.

## 5. Action list

All actions take `Connection` (VdbConnection) as the first input and return `Status` (VdbStatus)
as an output, in addition to what is listed.

### 5.1 Connection and discovery

| Action | Inputs | Outputs | Notes |
|---|---|---|---|
| `TestConnection` | Connection | Status, ServerInfo (Text) | Cheapest authenticated call per provider (list indexes, `/healthz` + auth check, `SELECT 1` with `vector` extension check) |
| `GetCapabilities` | Provider | Capabilities | Static, no network call. Lets apps hide unsupported UI. |

### 5.2 Collection (index) management

| Action | Inputs | Outputs | Notes |
|---|---|---|---|
| `CreateCollection` | Connection, Spec | Status | Pinecone index, Qdrant collection, Weaviate class, Milvus collection, pgvector table + HNSW index, Mongo collection + `vectorSearch` index, Azure index, Elastic index with `dense_vector`, Redis FT index |
| `DeleteCollection` | Connection, Name | Status | Destructive; client library asks for an explicit `Confirm` flag |
| `ListCollections` | Connection | Status, Names (List of Text) | |
| `DescribeCollection` | Connection, Name | Status, Spec, RecordCount (Long Integer), IsReady (Boolean) | `IsReady` matters for Pinecone / Mongo / Azure, where index build is async |
| `CollectionExists` | Connection, Name | Status, Exists | |

### 5.3 Records

| Action | Inputs | Outputs | Notes |
|---|---|---|---|
| `Upsert` | Connection, Collection, Namespace?, Records (List of VdbRecord), FieldMapping? | Status, UpsertedCount | Batches internally to the provider's max batch size |
| `Fetch` | Connection, Collection, Namespace?, Ids (List of Text), IncludeVectors? | Status, Records | |
| `Delete` | Connection, Collection, Namespace?, Ids | Status, DeletedCount (−1 when the provider doesn't report it) | |
| `DeleteByFilter` | Connection, Collection, Namespace?, FilterJson | Status, DeletedCount | Rejects an empty filter (use `DeleteCollection` or `DeleteNamespace`) |
| `UpdateMetadata` | Connection, Collection, Namespace?, Id, MetadataJson, Merge (Boolean) | Status | Partial update where the provider supports it, else fetch + upsert |
| `Count` | Connection, Collection, Namespace?, FilterJson? | Status, Count (Long Integer) | |
| `DeleteNamespace` | Connection, Collection, Namespace | Status | Pinecone namespace, Weaviate tenant, Milvus partition; else delete by `_namespace` filter |

### 5.4 Search

| Action | Inputs | Outputs | Notes |
|---|---|---|---|
| `Query` | Connection, Query | Status, Matches (List of VdbMatch) | Dense similarity search with optional filter |
| `HybridQuery` | Connection, Query | Status, Matches | Dense + keyword/sparse. Returns `Unsupported` where the provider has no hybrid mode. |
| `QueryByText` | Connection, Query | Status, Matches | Only for collections with server-side embedding (Pinecone integrated, Weaviate vectorizer, Azure integrated vectorization). `Unsupported` elsewhere. |
| `QueryBatch` | Connection, Queries (List of VdbQuery) | Status, Results (List of VdbMatchList) | Runs queries in parallel within the time budget |

### 5.5 Utilities (no network)

| Action | Purpose |
|---|---|
| `EncodeVector` / `DecodeVector` | List of Decimal ↔ base64 float32 |
| `ValidateFilter` | Parses a portable filter and, for a given provider, returns the native filter text or the unsupported operator |
| `BuildMetadataJson` | Builds metadata JSON from a list of key/value/type structures, for low-code users who don't want to write JSON |

That is 21 actions.

## 6. Cross-cutting design

### 6.1 Portable filter language
A small JSON dialect modelled on the Mongo/Pinecone style, which most developers already know:

```json
{ "$and": [
    { "category": { "$eq": "invoice" } },
    { "year":     { "$gte": 2024 } },
    { "tags":     { "$in": ["urgent", "eu"] } }
] }
```

Operators v1: `$eq $ne $gt $gte $lt $lte $in $nin $exists $and $or $not`. `FilterTranslator`
compiles it to each provider's form: Pinecone metadata filter (as-is), Qdrant `must/should/must_not`,
Weaviate `where`, Milvus boolean expression, SQL `WHERE` on JSONB with parameters (never string
concatenation), Mongo `$vectorSearch.filter`, Azure OData `$filter`, Elastic `bool` query, Redis
query syntax (TAG / NUMERIC fields). Operators a provider lacks return `Unsupported` before any
network call.

### 6.2 Payload and time limits
- **5.5 MB payload.** A 1,536-dim vector as a JSON decimal list is roughly 25–30 KB; as
  `VectorBase64` it is about 8 KB. `Upsert` rejects requests estimated above 5 MB with
  `PayloadTooLarge` and a suggested batch size. The client library offers `UpsertInBatches`.
- **95 s execution.** One time budget per call (default 30 s, max 85 s) shared by all retries and
  internal batches. Retries: 429 / 5xx / network errors, exponential backoff with jitter,
  honour `Retry-After`, stop when the budget would be exceeded.
- **1 GB memory.** Stream JSON (System.Text.Json `Utf8JsonWriter`), no full-response buffering
  for `QueryBatch`.

### 6.3 ID mapping
ODC IDs are always text. Qdrant only accepts unsigned integers or UUIDs, so non-UUID IDs become a
deterministic UUIDv5 and the original is stored in payload field `_id`. Milvus and pgvector
collections created by the connector use a VarChar / text primary key. Redis keys are
`{collection}:{id}`.

### 6.4 Score normalization
`Score` is always the provider's raw value. `Similarity` maps to 0–1 when possible: cosine
distance → `1 − d`, cosine similarity → `(s + 1) / 2`, Euclidean → `1 / (1 + d)`; dot product
is left unnormalized (Similarity = Score) and documented as such. `MinScore` filters on
`Similarity`.

### 6.5 Namespaces / multi-tenancy
Native where available (Pinecone namespaces, Weaviate multi-tenancy, Milvus partition keys);
elsewhere a reserved `_namespace` metadata field with an automatic filter, so app code behaves the
same on every provider.

### 6.6 Security
- API keys and connection strings come from ODC Settings marked secret, read in the client
  library; never hard-coded in the demo.
- HTTPS / TLS required. `OptionsJson.allowInsecure=true` only for local testing and logged as a
  warning.
- Logs (ILogger, Information and above in ODC Portal) carry provider, action, latency, counts and
  status only; never vectors, metadata, keys or connection strings.
- SQL for pgvector is fully parameterized; table and column names are validated against
  `^[A-Za-z_][A-Za-z0-9_]{0,62}$` and quoted.

## 7. Provider notes and capability matrix (to verify per provider version)

| Provider | Transport | Namespaces | Hybrid | Server-side embedding | Notes |
|---|---|---|---|---|---|
| Pinecone | REST (control + data plane host) | Native | Sparse-dense | Integrated indexes | Index build async; data-plane host from `describe_index` |
| Qdrant | REST | `_namespace` filter | Sparse vectors + fusion (Query API) | Cloud inference (verify) | Point IDs: int or UUID (§6.3) |
| Weaviate | REST + GraphQL | Multi-tenancy | Native `hybrid` (alpha) | Vectorizer modules | Class names must start uppercase |
| Milvus / Zilliz | REST v2 | Partition key | Hybrid search (multi-vector + rerank) | Text embedding functions (verify) | Collection must be loaded before search |
| pgvector | Npgsql (TCP) | `_namespace` column | tsvector + vector, fused in SQL (RRF) | No | Needs `CREATE EXTENSION vector` rights; HNSW index |
| MongoDB Atlas | MongoDB.Driver (TCP) | `_namespace` filter | `$rankFusion` (verify tier/version) | Auto-embedding (verify) | Search index build async; filter fields must be in index definition |
| Azure AI Search | REST | `_namespace` filter | Native hybrid + semantic ranker | Integrated vectorization | Overlaps native ODC Data Fabric; keep for parity |
| Elasticsearch | REST | `_namespace` filter | RRF retriever | Inference endpoints (verify) | `dense_vector` + kNN |
| Redis (Stack / Cloud) | StackExchange.Redis (TCP) | Key prefix | Hybrid query (verify version) | No | FT index over HASH or JSON |

## 8. Build plan

### Repository layout (in this folder)
```
src/UniversalVectorDBConnector/                  # external library (net10.0)
  Interfaces/IUniversalVectorDBConnector.cs      # [OSInterface] Name = "UniversalVectorDBConnector"
  Implementations/UniversalVectorDBConnector.cs  # dispatcher
  Structures/                           # [OSStructure] structs from §4
  Adapters/<Provider>Adapter.cs
  Core/ (FilterTranslator, VectorCodec, IdMapper, ScoreNormalizer, Http, Errors)
  Resources/icon.png                    # IconResourceName, required for Forge
src/UniversalVectorDBConnector.UnitTests/        # xUnit v3, mocked HTTP
src/UniversalVectorDBConnector.IntegrationTests/ # runs against local containers
docker-compose.test.yml                 # qdrant, weaviate, milvus-standalone, pgvector,
                                        # mongodb-atlas-local, elasticsearch, redis-stack,
                                        # pinecone-local
docs/
```
Local tooling confirmed on this machine: .NET SDK 10.0.402 and Docker 29.8.2.

### Phases
1. **MVP:** core (structures, filter translator, codec, errors) + Qdrant, pgvector, Pinecone,
   Azure AI Search. Unit + container tests. Upload to the Personal tenant, build the demo app.
2. **Breadth:** Weaviate, Milvus/Zilliz, Elasticsearch; `HybridQuery` and `QueryByText` across
   providers that support them. Optional **LangChain-compatible payload layout** (`page_content`
   + `metadata`) so ODC apps and LangChain/LangGraph agents can share collections. The connector
   itself does not use LangChain; it runs in ODC's .NET runtime. Re-add ILogger logging (dropped
   from 0.1 to shrink the upload).
3. **Drivers + agents:** MongoDB Atlas, Redis; SearchBridge app for Data Fabric custom search
   (after verifying its contract); Forge listing (icon, descriptions, docs, demo video).

### Testing
- Unit: every filter operator × provider, codec round-trips, ID mapping, score normalization,
  payload estimator, retry/budget logic with a fake clock.
- Container integration: the same scenario suite (create → upsert 1k → filtered query → hybrid →
  delete by filter → drop) against each local provider, run in `aspnet:10.0` with `--memory=1g`.
- Live smoke: the demo app in the Personal tenant, calling the published library.

### Target tenant
Developed and tested on an ODC Personal Edition tenant with a single Development stage. The demo app
uses fixed sample vectors because that tenant has no embedding model connection.
(Tenant-specific identifiers are kept in private notes, outside this repository.)

### Tenant changes this plan will need (each needs Abdul's OK when we get there)
1. Upload + publish the `UniversalVectorDBConnector` external library.
2. Create + publish the `UniversalVectorDBConnector_Client` library and `UniversalVectorDBConnector_Demo` app.
3. (Phase 3) Create the SearchBridge app and a Data Fabric AI search connection.

## 9. Open questions

1. **Data Fabric custom search service contract:** what request/response shape and auth does it
   expect, and can an ODC app's exposed REST API serve it? Decides whether deliverable 4 is
   feasible.
2. **Private networks:** can external libraries reach self-hosted DBs through ODC Private Gateway,
   and does that cover raw TCP (Postgres, Mongo, Redis) or only HTTP?
3. **Outbound TCP from external libraries:** confirm non-HTTP sockets are allowed (needed for
   Npgsql, MongoDB.Driver, StackExchange.Redis). If not, drop those three or require an HTTP proxy.
4. **Agents as tools:** whether ODC agents can call these server actions directly as tools, which
   might make the SearchBridge unnecessary.
5. **Naming:** final Forge name and the `[OSInterface] Name` (it fixes the library key; changing it
   later needs `OriginalName`).
6. **License:** BSD-3-Clause (as OutSystems' own UltimatePDF) or MIT.
7. **Embeddings for the demo:** which embedding provider to use, since the tenant has no
   embedding model connection (see the tenant snapshot in §8).

# Forge listing — UniversalVectorDBConnector 0.1.0 (Preview)

Copy each section into the matching step of **ODC Portal → Forge → Submit to Forge**.
Asset: `UniversalVectorDBConnector` (External Library, key `fa1f315e-478f-6828-dfb5-8d15d5bb7b2d`), version **0.1.0**.

**Verified 2026-10-21:** live smoke test from ODC (demo app `UniversalVectorDBConnector_Demo` rev 4) against Qdrant Cloud.
TestConnection was green, CreateCollection worked, Upsert returned 12 records and Count showed 12. All 28 local tests (unit + live Qdrant) also pass.

---

## Name
Universal Vector DB Connector

## Short description (one line)
One set of ODC actions for vector databases: collections, upsert, filtered similarity search, fetch, count and delete. Preview: Qdrant today, more providers coming.

## Library type
External library (C# / .NET 10)

## Description (paste this into the wizard; 921 characters, limit 1000)

```
PREVIEW / WORK IN PROGRESS. Version 0.1 supports Qdrant (Qdrant Cloud or self-hosted). Pinecone, pgvector, Azure AI Search, Weaviate, Milvus, Elasticsearch, MongoDB Atlas and Redis are planned.

One set of ODC actions for vector databases, so you can build RAG, semantic search and "more like this" features without custom integration code. Switching provider later is a configuration change, not a rewrite.

Actions: TestConnection, CreateCollection, DeleteCollection, ListCollections, CollectionExists, Upsert, Query (similarity search with metadata filters), Fetch, Delete, DeleteByFilter, Count.

Highlights:
- One portable filter syntax, e.g. {"category":"invoice","year":{"$gte":2024}}
- Errors returned as a VdbStatus instead of exceptions
- Built for ODC limits: time budget with retries, compact base64 vectors
- Namespaces, automatic ID mapping, API keys never logged

Bring your own embeddings from any model.
```

## Long description (reference only; too long for the wizard's Description field)

**Preview / work in progress.** Version 0.1 supports **Qdrant** (Qdrant Cloud or self-hosted). Pinecone, pgvector, Azure AI Search, Weaviate, Milvus/Zilliz, Elasticsearch, MongoDB Atlas Vector Search and Redis are planned.

Build RAG, semantic search and "more like this" features in ODC without writing integration code for each vector database. Every action takes a `VdbConnection` (provider, endpoint, API key), so switching provider later is a configuration change, not a rewrite.

**What you get**
- **Collections:** CreateCollection (dimension, metric, metadata indexes), DeleteCollection, ListCollections, CollectionExists
- **Records:** Upsert (auto-batched), Fetch, Delete, DeleteByFilter, Count
- **Search:** Query, a similarity search with metadata filters, namespaces, Top K and a minimum score
- **Helpers:** TestConnection, GetCapabilities, EncodeVector / DecodeVector

**Built for ODC**
- **One portable filter language** for every provider, e.g. `{"category":"invoice","year":{"$gte":2024}}` (`$eq $ne $gt $gte $lt $lte $in $nin $exists $and $or $not`)
- **Errors as data:** every action returns a `VdbStatus` (Success, ErrorCode such as `Unauthorized`, `NotFound` or `RateLimited`, plus a message) instead of throwing
- **Stays inside ODC limits:** each call has a time budget (default 30 s, max 85 s) that covers its retries, and `VectorBase64` sends vectors about 5× smaller than decimal lists
- **Any text can be a record ID:** IDs are mapped to each provider's format automatically
- **Namespaces** keep several tenants or datasets in one collection
- **Secrets stay secret:** API keys are never logged

**Bring your own vectors.** Use any embedding model (ODC AI model connections, OpenAI, Cohere and so on) and pass the numbers in. Pair it with the Forge *ODC Chunking Library* to split documents first.

## Limitations (0.1 Preview)
- Only the `qdrant` provider is implemented; other providers return `Unsupported` with the message "planned for a later version".
- No hybrid (keyword + vector) search and no server-side embedding yet.
- Qdrant collections with several named vectors use the first one.
- No logging to ODC Portal yet (planned for 0.2); check `VdbStatus` for errors.
- The database must be reachable from ODC over HTTPS (for example Qdrant Cloud). A Qdrant running on your own PC is not reachable.

## Documentation

**Quick start (Qdrant Cloud, about 5 minutes)**
1. Create a free cluster at cloud.qdrant.io and copy its URL (add `:6333` if it has no port) and an API key.
2. In your app, add two Settings: `QdrantEndpoint` (Text) and `QdrantApiKey` (Text, **secret**), and set their values in ODC Portal.
3. Add the **UniversalVectorDBConnector** dependency.
4. Build a `VdbConnection`: Provider = `"qdrant"`, Endpoint = `Settings.QdrantEndpoint`, ApiKey = `Settings.QdrantApiKey`.
5. Call `TestConnection` and check `Status.Success`.
6. `CreateCollection` with Spec: Name `"docs"`, Dimension = your embedding size (e.g. 1536), Metric `"cosine"`, MetadataIndexesJson `{"category":"keyword"}`.
7. `Upsert` a list of `VdbRecord` (Id, Vector or VectorBase64, Text, MetadataJson).
8. `Query` with a `VdbQuery` (Collection, Vector, TopK, FilterJson) and read `Matches` (Id, Score, Similarity 0-1, Text, MetadataJson).

**Best practices**
- Call the library only from **server actions**, and never pass the API key to the client.
- Always check `Status.Success`; on `RateLimited`, wait `Status.RetryAfterMs` before retrying.
- Keep each Upsert under about 5 MB (ODC payload limit). With 1536-dimension vectors that's about 150 records as decimal lists, or about 600 as `VectorBase64`.
- Index the metadata fields you filter on (`MetadataIndexesJson`); Qdrant Cloud may require it.
- Keys starting with `_` in MetadataJson are reserved by the connector.

## License
BSD-3-Clause (chosen by Abdul 2026-10-21; full text in the repository's `LICENSE` file)

## Images
- Icon: embedded in the library (placeholder, to be replaced before 0.2).
- Screenshot (now): the demo app's **Overview** screen after "Load sample data" (Connected + Records: 12).
- More screenshots (Search, Documents) to follow with the rest of the demo app.

## Team / contact
Abdul *(fill in the contact details in the wizard)*

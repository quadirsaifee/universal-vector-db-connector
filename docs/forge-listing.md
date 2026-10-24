# Forge listing — UniversalVectorDBConnector 0.2.0 (Preview)

Copy each section into the matching step of **ODC Portal → Forge → Submit to Forge** (new version of the existing asset).
Asset: `UniversalVectorDBConnector` (External Library), version **0.2.0**.

**Verified 2026-10-23 (local):** all 46 tests pass: unit tests, an offline fake of the Pinecone API, live Qdrant, and
live Pinecone (serverless, aws us-east-1, Starter plan).
**ODC smoke test (0.2.0), passed 2026-10-23:** the demo app, running on the released 0.2.0 library, connected to Pinecone, created the collection, upserted 12 records, and Count showed 12.
0.1.0 was verified on 2026-10-21 from the demo app against Qdrant Cloud (12 records upserted, count 12).

---

## Name
Universal Vector DB Connector

## Short description (one line)
One set of ODC actions for vector databases: collections, upsert, filtered similarity search, fetch, count and delete. Preview: Qdrant and Pinecone today, more providers coming.

## Library type
External library (C# / .NET 10)

## Description (paste this into the wizard; 923 characters, limit 1000)

```
PREVIEW / WORK IN PROGRESS. Version 0.2 supports Qdrant (Cloud or self-hosted) and Pinecone (serverless). pgvector, Azure AI Search, Weaviate, Milvus, Elasticsearch, MongoDB Atlas and Redis are planned.

One set of ODC actions for vector databases, so you can build RAG, semantic search and "more like this" features without custom integration code. Switching provider is a configuration change, not a rewrite.

Actions: TestConnection, CreateCollection, DeleteCollection, ListCollections, CollectionExists, Upsert, Query (similarity search with metadata filters), Fetch, Delete, DeleteByFilter, Count.

Highlights:
- One portable filter syntax, e.g. {"category":"invoice","year":{"$gte":2024}}
- Errors returned as a VdbStatus instead of exceptions
- Built for ODC limits: time budget with retries, compact base64 vectors
- Namespaces, automatic ID mapping, API keys never logged

Bring your own embeddings from any model.
```

## What's new in 0.2.0 (release notes)

```
New provider: Pinecone (serverless, REST API 2026-07). Set Provider = "pinecone" and Endpoint = "https://api.pinecone.io".
- CreateCollection creates a serverless index (default aws / us-east-1, the free Starter region) and waits until it is ready. Pick another cloud/region with OptionsJson.
- Portable filters are translated for Pinecone. $not over a whole filter is supported.
- Any text can be a record ID: IDs that are not ASCII or are longer than 512 characters are mapped automatically.
- Euclidean scores are converted to a 0-1 Similarity, like the other metrics.
No changes to existing actions or structures; Qdrant apps keep working as before.
```

## Long description (reference only; too long for the wizard's Description field)

**Preview / work in progress.** Version 0.2 supports **Qdrant** (Qdrant Cloud or self-hosted) and **Pinecone** (serverless). pgvector, Azure AI Search, Weaviate, Milvus/Zilliz, Elasticsearch, MongoDB Atlas Vector Search and Redis are planned.

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

## Limitations (0.2 Preview)
- Implemented providers: `qdrant` and `pinecone`. Other providers return `Unsupported` with the message "planned for a later version".
- No hybrid (keyword + vector) search and no server-side embedding yet.
- Qdrant collections with several named vectors use the first one.
- Pinecone:
  - Index names must be lowercase letters, digits or `-`.
  - Metadata must be flat (text, numbers, true/false, lists of text).
  - Range filters compare numbers only (store dates as numbers).
  - An empty Namespace means the default namespace.
  - `Count` with a filter returns `Unsupported`.
  - New records can take a few seconds to appear in searches.
- No logging to ODC Portal yet; check `VdbStatus` for errors.
- The database must be reachable from ODC over HTTPS (for example Qdrant Cloud or Pinecone). A database running on your own PC is not reachable.

## Documentation

**Quick start: Qdrant Cloud (about 5 minutes)**
1. Create a free cluster at cloud.qdrant.io and copy its URL (add `:6333` if it has no port) and an API key.
2. In your app, add two Settings: `VdbEndpoint` (Text) and `VdbApiKey` (Text, **secret**), and set their values in ODC Portal.
3. Add the **UniversalVectorDBConnector** dependency.
4. Build a `VdbConnection`: Provider = `"qdrant"`, Endpoint = `Settings.VdbEndpoint`, ApiKey = `Settings.VdbApiKey`.
5. Call `TestConnection` and check `Status.Success`.
6. `CreateCollection` with Spec: Name `"docs"`, Dimension = your embedding size (e.g. 1536), Metric `"cosine"`, MetadataIndexesJson `{"category":"keyword"}`.
7. `Upsert` a list of `VdbRecord` (Id, Vector or VectorBase64, Text, MetadataJson).
8. `Query` with a `VdbQuery` (Collection, Vector, TopK, FilterJson) and read `Matches` (Id, Score, Similarity 0-1, Text, MetadataJson).

**Quick start: Pinecone (about 5 minutes)**
1. Sign up at app.pinecone.io (the free Starter plan works) and create an API key under **API Keys**.
2. Set the Settings: `VdbEndpoint` = `https://api.pinecone.io`, `VdbApiKey` = your key (**secret**).
3. Build a `VdbConnection` with Provider = `"pinecone"` and call `TestConnection`.
4. `CreateCollection` with a lowercase name such as `"docs"`, your embedding Dimension and Metric `"cosine"`. It waits until the index is ready. For another location, set OptionsJson to e.g. `{"cloud":"gcp","region":"us-central1"}`.
5. `Upsert`, `Query`, `Fetch`, `Delete` and `Count` work as in the Qdrant quick start. No metadata indexes are needed: Pinecone indexes every field.

**Best practices**
- Call the library only from **server actions**, and never pass the API key to the client.
- Always check `Status.Success`; on `RateLimited`, wait `Status.RetryAfterMs` before retrying.
- Keep each Upsert under about 5 MB (ODC payload limit). With 1536-dimension vectors that's about 150 records as decimal lists, or about 600 as `VectorBase64`.
- Qdrant: index the metadata fields you filter on (`MetadataIndexesJson`); Qdrant Cloud may require it.
- Use `GetCapabilities(Provider)` to check what a provider supports before calling an action.
- Keys starting with `_` in MetadataJson are reserved by the connector.

## License
BSD-3-Clause (chosen by Abdul 2026-10-21; full text in the repository's `LICENSE` file)

## Images
- Icon: embedded in the library (still the placeholder; a real icon is planned).
- Screenshot: the demo app's **Overview** screen after "Load sample data" (Connected + Records: 12).
- More screenshots (Search, Documents) to follow with the rest of the demo app.

## Team / contact
Abdul *(fill in the contact details in the wizard)*

//-----------------------------------------------------------------------
// <copyright file="Documents.cs" company="Hibernating Rhinos LTD">
//     Copyright (c) Hibernating Rhinos LTD. All rights reserved.
// </copyright>
//-----------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Isam.Esent.Interop;
using Raven.Abstractions;
using Raven.Abstractions.Data;
using Raven.Abstractions.Exceptions;
using Raven.Abstractions.Extensions;
using Raven.Abstractions.Logging;
using Raven.Abstractions.Util;
using Raven.Database.Extensions;
using Raven.Database.Impl;
using Raven.Database.Storage;
using Raven.Json.Linq;

namespace Raven.Storage.Esent.StorageActions
{
	public partial class DocumentStorageActions : IDocumentStorageActions, ITransactionStorageActions
	{
		public long GetDocumentsCount()
		{
			if (Api.TryMoveFirst(session, Details))
				return Api.RetrieveColumnAsInt32(session, Details, tableColumnsCache.DetailsColumns["document_count"]).Value;
			return 0;
		}

		public JsonDocument DocumentByKey(string key, TransactionInformation transactionInformation)
		{
			return DocumentByKeyInternal(key, transactionInformation, (metadata, createDocument) =>
			{
				Debug.Assert(metadata.Etag != null);
				return new JsonDocument
				{
					DataAsJson = createDocument(metadata.Key, metadata.Etag.Value, metadata.Metadata),
					Etag = metadata.Etag,
					Key = metadata.Key,
					LastModified = metadata.LastModified,
					Metadata = metadata.Metadata,
					NonAuthoritativeInformation = metadata.NonAuthoritativeInformation,
				};
			});
		}

		public JsonDocumentMetadata DocumentMetadataByKey(string key, TransactionInformation transactionInformation)
		{
			return DocumentByKeyInternal(key, transactionInformation, (metadata, func) => metadata);
		}

		private T DocumentByKeyInternal<T>(string key, TransactionInformation transactionInformation, Func<JsonDocumentMetadata, Func<string, Guid, RavenJObject, RavenJObject>, T> createResult)
			where T : class
		{
			bool existsInTx = IsDocumentModifiedInsideTransaction(key);

			if (transactionInformation != null && existsInTx)
			{
				var txId = Api.RetrieveColumn(session, DocumentsModifiedByTransactions, tableColumnsCache.DocumentsModifiedByTransactionsColumns["locked_by_transaction"]);
				if (new Guid(txId) == transactionInformation.Id)
				{
					if (Api.RetrieveColumnAsBoolean(session, DocumentsModifiedByTransactions, tableColumnsCache.DocumentsModifiedByTransactionsColumns["delete_document"]) == true)
					{
						logger.Debug("Document with key '{0}' was deleted in transaction: {1}", key, transactionInformation.Id);
						return null;
					}
					var etag = Api.RetrieveColumn(session, DocumentsModifiedByTransactions, tableColumnsCache.DocumentsModifiedByTransactionsColumns["etag"]).TransfromToGuidWithProperSorting();

					RavenJObject metadata = ReadDocumentMetadataInTransaction(key, etag);


					logger.Debug("Document with key '{0}' was found in transaction: {1}", key, transactionInformation.Id);
					var lastModified = Api.RetrieveColumnAsInt64(session, DocumentsModifiedByTransactions, tableColumnsCache.DocumentsModifiedByTransactionsColumns["last_modified"]).Value;
					return createResult(new JsonDocumentMetadata()
					{
						NonAuthoritativeInformation = false,// we are the transaction, therefor we are Authoritative
						Etag = etag,
						LastModified = DateTime.FromBinary(lastModified),
						Key = Api.RetrieveColumnAsString(session, DocumentsModifiedByTransactions, tableColumnsCache.DocumentsModifiedByTransactionsColumns["key"], Encoding.Unicode),
						Metadata = metadata
					}, ReadDocumentDataInTransaction);
				}
			}

			Api.JetSetCurrentIndex(session, Documents, "by_key");
			Api.MakeKey(session, Documents, key, Encoding.Unicode, MakeKeyGrbit.NewKey);
			if (Api.TrySeek(session, Documents, SeekGrbit.SeekEQ) == false)
			{
				if (existsInTx)
				{
					logger.Debug("Committed document with key '{0}' was not found, but exists in a separate transaction", key);
					return createResult(new JsonDocumentMetadata
					{
						Etag = Guid.Empty,
						Key = key,
						Metadata = new RavenJObject { { Constants.RavenDocumentDoesNotExists, true } },
						NonAuthoritativeInformation = true,
						LastModified = DateTime.MinValue,
					}, (docKey, etag, metadata) => new RavenJObject());
				}
				logger.Debug("Document with key '{0}' was not found", key);
				return null;
			}
			var existingEtag = Api.RetrieveColumn(session, Documents, tableColumnsCache.DocumentsColumns["etag"]).TransfromToGuidWithProperSorting();
			logger.Debug("Document with key '{0}' was found", key);
			var lastModifiedInt64 = Api.RetrieveColumnAsInt64(session, Documents, tableColumnsCache.DocumentsColumns["last_modified"]).Value;
			return createResult(new JsonDocumentMetadata()
			{
				Etag = existingEtag,
				NonAuthoritativeInformation = existsInTx,
				LastModified = DateTime.FromBinary(lastModifiedInt64),
				Key = Api.RetrieveColumnAsString(session, Documents, tableColumnsCache.DocumentsColumns["key"], Encoding.Unicode),
				Metadata = ReadDocumentMetadata(key, existingEtag)
			}, ReadDocumentData);
		}

		private RavenJObject ReadDocumentMetadataInTransaction(string key, Guid etag)
		{
			var cachedDocument = cacher.GetCachedDocument(key, etag);
			if (cachedDocument != null)
			{
				return cachedDocument.Metadata;
			}

			return Api.RetrieveColumn(session, DocumentsModifiedByTransactions, tableColumnsCache.DocumentsModifiedByTransactionsColumns["metadata"]).ToJObject();

		}

		private RavenJObject ReadDocumentDataInTransaction(string key, Guid etag, RavenJObject metadata)
		{
			var cachedDocument = cacher.GetCachedDocument(key, etag);
			if (cachedDocument != null)
			{
				return cachedDocument.Document;
			}

			using (Stream stream = new BufferedStream(new ColumnStream(session, DocumentsModifiedByTransactions, tableColumnsCache.DocumentsModifiedByTransactionsColumns["data"])))
			{
				var size = stream.Length;
				using (var aggregate = documentCodecs.Aggregate(stream, (bytes, codec) => codec.Decode(key, metadata, bytes)))
				{
					var data = aggregate.ToJObject();
					cacher.SetCachedDocument(key, etag, data, metadata, (int)size);
					return data;
				}
			}
		}

		private RavenJObject ReadDocumentMetadata(string key, Guid existingEtag)
		{
			var existingCachedDocument = cacher.GetCachedDocument(key, existingEtag);
			if (existingCachedDocument != null)
				return existingCachedDocument.Metadata;

			return Api.RetrieveColumn(session, Documents, tableColumnsCache.DocumentsColumns["metadata"]).ToJObject();
		}

		private RavenJObject ReadDocumentData(string key, Guid existingEtag, RavenJObject metadata)
		{
			var existingCachedDocument = cacher.GetCachedDocument(key, existingEtag);
			if (existingCachedDocument != null)
				return existingCachedDocument.Document;


			using (Stream stream = new BufferedStream(new ColumnStream(session, Documents, tableColumnsCache.DocumentsColumns["data"])))
			{
				var size = stream.Length;
				using (var columnStream = documentCodecs.Aggregate(stream, (dataStream, codec) => codec.Decode(key, metadata, dataStream)))
				{
					var data = columnStream.ToJObject();

					cacher.SetCachedDocument(key, existingEtag, data, metadata, (int)size);

					return data;
				}
			}
		}

		public IEnumerable<JsonDocument> GetDocumentsByReverseUpdateOrder(int start, int take)
		{
			Api.JetSetCurrentIndex(session, Documents, "by_etag");
			Api.MoveAfterLast(session, Documents);
			if (TryMoveTableRecords(Documents, start, backward: true))
				return Enumerable.Empty<JsonDocument>();
			var optimizer = new OptimizedIndexReader(take);
			while (Api.TryMovePrevious(session, Documents) && optimizer.Count < take)
			{
				optimizer.Add(Session, Documents);
			}

			return optimizer.Select(Session, Documents, ReadCurrentDocument);
		}

		private bool TryMoveTableRecords(Table table, int start, bool backward)
		{
			if (start <= 0)
				return false;
			if (backward)
				start *= -1;
			try
			{
				Api.JetMove(session, table, start, MoveGrbit.None);
			}
			catch (EsentErrorException e)
			{
				if (e.Error == JET_err.NoCurrentRecord)
				{
					return true;
				}
				throw;
			}
			return false;
		}

		private JsonDocument ReadCurrentDocument()
		{
			return ReadCurrentDocument(true);
		}

		private JsonDocument ReadCurrentDocument(bool checkTransactionStatus)
		{
			int docSize;

			var metadataBuffer = Api.RetrieveColumn(session, Documents, tableColumnsCache.DocumentsColumns["metadata"]);
			var metadata = metadataBuffer.ToJObject();
			var key = Api.RetrieveColumnAsString(session, Documents, tableColumnsCache.DocumentsColumns["key"], Encoding.Unicode);

			RavenJObject dataAsJson;
			using (Stream stream = new BufferedStream(new ColumnStream(session, Documents, tableColumnsCache.DocumentsColumns["data"])))
			{
				using (var aggregate = documentCodecs.Aggregate(stream, (bytes, codec) => codec.Decode(key, metadata, bytes)))
				{
					dataAsJson = aggregate.ToJObject();
					docSize = (int)stream.Position;
				}
			}

			bool isDocumentModifiedInsideTransaction = false;
			if (checkTransactionStatus)
				isDocumentModifiedInsideTransaction = IsDocumentModifiedInsideTransaction(key);
			var lastModified = Api.RetrieveColumnAsInt64(session, Documents, tableColumnsCache.DocumentsColumns["last_modified"]).Value;
			return new JsonDocument
			{
				SerializedSizeOnDisk = metadataBuffer.Length + docSize,
				Key = key,
				DataAsJson = dataAsJson,
				NonAuthoritativeInformation = isDocumentModifiedInsideTransaction,
				LastModified = DateTime.FromBinary(lastModified),
				Etag = Api.RetrieveColumn(session, Documents, tableColumnsCache.DocumentsColumns["etag"]).TransfromToGuidWithProperSorting(),
				Metadata = metadata
			};
		}


		public IEnumerable<JsonDocument> GetDocumentsAfter(Guid etag, int take, long? maxSize = null, Guid? untilEtag = null)
		{
			Api.JetSetCurrentIndex(session, Documents, "by_etag");
			Api.MakeKey(session, Documents, etag.TransformToValueForEsentSorting(), MakeKeyGrbit.NewKey);
			if (Api.TrySeek(session, Documents, SeekGrbit.SeekGT) == false)
				yield break;
			long totalSize = 0;
			int count = 0;
			do
			{
				if (untilEtag != null && count > 0)
				{
					var docEtag = Api.RetrieveColumn(session, Documents, tableColumnsCache.DocumentsColumns["etag"]).TransfromToGuidWithProperSorting();
					if (Etag.IsGreaterThanOrEqual(docEtag, untilEtag.Value))
						yield break;
				}
				var readCurrentDocument = ReadCurrentDocument(checkTransactionStatus: false);
				totalSize += readCurrentDocument.SerializedSizeOnDisk;
				if (maxSize != null && totalSize > maxSize.Value)
				{
					yield return readCurrentDocument;
					yield break;
				}
				yield return readCurrentDocument;
				count++;
			} while (Api.TryMoveNext(session, Documents) && count < take);
		}

		public Guid GetBestNextDocumentEtag(Guid etag)
		{
			Api.JetSetCurrentIndex(session, Documents, "by_etag");
			Api.MakeKey(session, Documents, etag.TransformToValueForEsentSorting(), MakeKeyGrbit.NewKey);
			if (Api.TrySeek(session, Documents, SeekGrbit.SeekGT) == false)
				return etag;


			var val = Api.RetrieveColumn(session, Documents, tableColumnsCache.DocumentsColumns["etag"],
										 RetrieveColumnGrbit.RetrieveFromIndex, null);
			return new Guid(val);
		}

		public IEnumerable<JsonDocument> GetDocumentsWithIdStartingWith(string idPrefix, int start, int take)
		{
			Api.JetSetCurrentIndex(session, Documents, "by_key");
			Api.MakeKey(session, Documents, idPrefix, Encoding.Unicode, MakeKeyGrbit.NewKey);
			if (Api.TrySeek(session, Documents, SeekGrbit.SeekGE) == false)
				return Enumerable.Empty<JsonDocument>();

			Api.MakeKey(session, Documents, idPrefix, Encoding.Unicode, MakeKeyGrbit.NewKey | MakeKeyGrbit.SubStrLimit);
			if (Api.TrySetIndexRange(session, Documents, SetIndexRangeGrbit.RangeUpperLimit | SetIndexRangeGrbit.RangeInclusive) == false)
				return Enumerable.Empty<JsonDocument>();

			if (TryMoveTableRecords(Documents, start, backward: false))
				return Enumerable.Empty<JsonDocument>();

			var optimizer = new OptimizedIndexReader(take);
			do
			{
				optimizer.Add(Session, Documents);
			} while (Api.TryMoveNext(session, Documents) && optimizer.Count < take);

			return optimizer.Select(Session, Documents, ReadCurrentDocument);
		}

		public void TouchDocument(string key, out Guid? preTouchEtag, out Guid? afterTouchEtag)
		{
			Api.JetSetCurrentIndex(session, Documents, "by_key");
			Api.MakeKey(session, Documents, key, Encoding.Unicode, MakeKeyGrbit.NewKey);
			var isUpdate = Api.TrySeek(session, Documents, SeekGrbit.SeekEQ);
			if (isUpdate == false)
			{
				preTouchEtag = null;
				afterTouchEtag = null;
				return;
			}

			preTouchEtag = Api.RetrieveColumn(session, Documents, tableColumnsCache.DocumentsColumns["etag"]).TransfromToGuidWithProperSorting();
			Guid newEtag = uuidGenerator.CreateSequentialUuid(UuidType.Documents);
			afterTouchEtag = newEtag;
			using (var update = new Update(session, Documents, JET_prep.Replace))
			{
				Api.SetColumn(session, Documents, tableColumnsCache.DocumentsColumns["etag"], newEtag.TransformToValueForEsentSorting());
				update.Save();
			}
		}

		public AddDocumentResult PutDocumentMetadata(string key, RavenJObject metadata)
		{
			Api.JetSetCurrentIndex(session, Documents, "by_key");
			Api.MakeKey(session, Documents, key, Encoding.Unicode, MakeKeyGrbit.NewKey);
			var isUpdate = Api.TrySeek(session, Documents, SeekGrbit.SeekEQ);
			if (isUpdate == false)
				throw new InvalidOperationException("Updating document metadata is only valid for existing documents, but " + key +
													" does not exists");

			Guid newEtag = uuidGenerator.CreateSequentialUuid(UuidType.Documents);
			DateTime savedAt = SystemTime.UtcNow;
			using (var update = new Update(session, Documents, JET_prep.Replace))
			{
				Api.SetColumn(session, Documents, tableColumnsCache.DocumentsColumns["etag"], newEtag.TransformToValueForEsentSorting());
				Api.SetColumn(session, Documents, tableColumnsCache.DocumentsColumns["last_modified"], savedAt.ToBinary());

				using (var columnStream = new ColumnStream(session, Documents, tableColumnsCache.DocumentsColumns["metadata"]))
				{
					columnStream.SetLength(0); // always updating here
					using (Stream stream = new BufferedStream(columnStream))
					{
						metadata.WriteTo(stream);
						stream.Flush();
					}
				}

				update.Save();
			}
			return new AddDocumentResult
			{
				Etag = newEtag,
				SavedAt = savedAt,
				Updated = true
			};
		}

		public void IncrementDocumentCount(int value)
		{
			if (Api.TryMoveFirst(session, Details))
				Api.EscrowUpdate(session, Details, tableColumnsCache.DetailsColumns["document_count"], value);
		}

		public AddDocumentResult AddDocument(string key, Guid? etag, RavenJObject data, RavenJObject metadata)
		{
			if (key != null && Encoding.Unicode.GetByteCount(key) >= 2048)
				throw new ArgumentException(string.Format("The key must be a maximum of 2,048 bytes in Unicode, 1,024 characters, key is: '{0}'", key), "key");

			Api.JetSetCurrentIndex(session, Documents, "by_key");
			Api.MakeKey(session, Documents, key, Encoding.Unicode, MakeKeyGrbit.NewKey);
			var isUpdate = Api.TrySeek(session, Documents, SeekGrbit.SeekEQ);
			if (isUpdate)
			{
				EnsureNotLockedByTransaction(key, null);
				EnsureDocumentEtagMatch(key, etag, "PUT");

			}
			else
			{
				if (etag != null && etag != Guid.Empty) // expected something to be there.
					throw new ConcurrencyException("PUT attempted on document '" + key +
												   "' using a non current etag (document deleted)")
					{
						ExpectedETag = etag.Value,
                        Key = key
					};
				EnsureDocumentIsNotCreatedInAnotherTransaction(key, Guid.NewGuid());
				if (Api.TryMoveFirst(session, Details))
					Api.EscrowUpdate(session, Details, tableColumnsCache.DetailsColumns["document_count"], 1);
			}
			Guid newEtag = uuidGenerator.CreateSequentialUuid(UuidType.Documents);


			DateTime savedAt;
			using (var update = new Update(session, Documents, isUpdate ? JET_prep.Replace : JET_prep.Insert))
			{
				Api.SetColumn(session, Documents, tableColumnsCache.DocumentsColumns["key"], key, Encoding.Unicode);
				using (var columnStream = new ColumnStream(session, Documents, tableColumnsCache.DocumentsColumns["data"]))
				{
					if (isUpdate)
						columnStream.SetLength(0); // empty the existing value, since we are going to overwrite the entire thing
					using (Stream stream = new BufferedStream(columnStream))
					using (var finalStream = documentCodecs.Aggregate(stream, (current, codec) => codec.Encode(key, data, metadata, current)))
					{
						data.WriteTo(finalStream);
						finalStream.Flush();
					}
				}

				Api.SetColumn(session, Documents, tableColumnsCache.DocumentsColumns["etag"], newEtag.TransformToValueForEsentSorting());
				savedAt = SystemTime.UtcNow;
				Api.SetColumn(session, Documents, tableColumnsCache.DocumentsColumns["last_modified"], savedAt.ToBinary());

				using (var columnStream = new ColumnStream(session, Documents, tableColumnsCache.DocumentsColumns["metadata"]))
				{
					if (isUpdate)
						columnStream.SetLength(0);
					using (Stream stream = new BufferedStream(columnStream))
					{
						metadata.WriteTo(stream);
						stream.Flush();
					}
				}

				update.Save();
			}

			logger.Debug("Inserted a new document with key '{0}', update: {1}, ",
							   key, isUpdate);

			cacher.RemoveCachedDocument(key, newEtag);
			return new AddDocumentResult
			{
				Etag = newEtag,
				SavedAt = savedAt,
				Updated = isUpdate
			};
		}

		public AddDocumentResult InsertDocument(string key, RavenJObject data, RavenJObject metadata, bool checkForUpdates)
		{
			var prep = JET_prep.Insert;
			bool isUpdate = false;
			if (checkForUpdates)
			{
				Api.JetSetCurrentIndex(session, Documents, "by_key");
				Api.MakeKey(session, Documents, key, Encoding.Unicode, MakeKeyGrbit.NewKey);
				isUpdate = Api.TrySeek(session, Documents, SeekGrbit.SeekEQ);
				if (isUpdate)
				{
					prep = JET_prep.Replace;
				}
			}
			using (var update = new Update(session, Documents, prep))
			{
				Api.SetColumn(session, Documents, tableColumnsCache.DocumentsColumns["key"], key, Encoding.Unicode);
				using (var columnStream = new ColumnStream(session, Documents, tableColumnsCache.DocumentsColumns["data"]))
				{
					if (isUpdate)
						columnStream.SetLength(0);
					using (Stream stream = new BufferedStream(columnStream))
					using (var finalStream = documentCodecs.Aggregate(stream, (current, codec) => codec.Encode(key, data, metadata, current)))
					{
						data.WriteTo(finalStream);
						finalStream.Flush();
					}
				}
				Guid newEtag = uuidGenerator.CreateSequentialUuid(UuidType.Documents);
				Api.SetColumn(session, Documents, tableColumnsCache.DocumentsColumns["etag"], newEtag.TransformToValueForEsentSorting());
				DateTime savedAt = SystemTime.UtcNow;
				Api.SetColumn(session, Documents, tableColumnsCache.DocumentsColumns["last_modified"], savedAt.ToBinary());

				using (var columnStream = new ColumnStream(session, Documents, tableColumnsCache.DocumentsColumns["metadata"]))
				{
					if (isUpdate)
						columnStream.SetLength(0);
					using (Stream stream = new BufferedStream(columnStream))
					{
						metadata.WriteTo(stream);
						stream.Flush();
					}
				}

				update.Save();

				return new AddDocumentResult
				{
					Etag = newEtag,
					SavedAt = savedAt,
					Updated = isUpdate
				};
			}
		}

		public Guid AddDocumentInTransaction(string key, Guid? etag, RavenJObject data, RavenJObject metadata, TransactionInformation transactionInformation)
		{
			Api.JetSetCurrentIndex(session, Documents, "by_key");
			Api.MakeKey(session, Documents, key, Encoding.Unicode, MakeKeyGrbit.NewKey);
			var isUpdate = Api.TrySeek(session, Documents, SeekGrbit.SeekEQ);
			if (isUpdate)
			{
				EnsureNotLockedByTransaction(key, transactionInformation.Id);
				EnsureDocumentEtagMatchInTransaction(key, etag);
				using (var update = new Update(session, Documents, JET_prep.Replace))
				{
					Api.SetColumn(session, Documents, tableColumnsCache.DocumentsColumns["locked_by_transaction"], transactionInformation.Id.ToByteArray());
					update.Save();
				}
			}
			else
			{
				EnsureDocumentIsNotCreatedInAnotherTransaction(key, transactionInformation.Id);
			}
			EnsureTransactionExists(transactionInformation);
			Guid newEtag = uuidGenerator.CreateSequentialUuid(UuidType.DocumentTransactions);

			Api.JetSetCurrentIndex(session, DocumentsModifiedByTransactions, "by_key");
			Api.MakeKey(session, DocumentsModifiedByTransactions, key, Encoding.Unicode, MakeKeyGrbit.NewKey);
			var isUpdateInTransaction = Api.TrySeek(session, DocumentsModifiedByTransactions, SeekGrbit.SeekEQ);

			using (var update = new Update(session, DocumentsModifiedByTransactions, isUpdateInTransaction ? JET_prep.Replace : JET_prep.Insert))
			{
				Api.SetColumn(session, DocumentsModifiedByTransactions, tableColumnsCache.DocumentsModifiedByTransactionsColumns["key"], key, Encoding.Unicode);

				using (var columnStream = new ColumnStream(session, DocumentsModifiedByTransactions, tableColumnsCache.DocumentsModifiedByTransactionsColumns["data"]))
				{
					if (isUpdate)
						columnStream.SetLength(0);
					using (Stream stream = new BufferedStream(columnStream))
					using (var finalStream = documentCodecs.Aggregate(stream, (current, codec) => codec.Encode(key, data, metadata, current)))
					{
						data.WriteTo(finalStream);
						finalStream.Flush();
					}
				}
				Api.SetColumn(session, DocumentsModifiedByTransactions,
							  tableColumnsCache.DocumentsModifiedByTransactionsColumns["etag"],
							  newEtag.TransformToValueForEsentSorting());

				using (var columnStream = new ColumnStream(session, DocumentsModifiedByTransactions, tableColumnsCache.DocumentsModifiedByTransactionsColumns["metadata"]))
				{
					if (isUpdate)
						columnStream.SetLength(0);
					using (Stream stream = new BufferedStream(columnStream))
					{
						metadata.WriteTo(stream);
						stream.Flush();
					}
				}

				Api.SetColumn(session, DocumentsModifiedByTransactions, tableColumnsCache.DocumentsModifiedByTransactionsColumns["last_modified"], SystemTime.UtcNow.ToBinary());
				Api.SetColumn(session, DocumentsModifiedByTransactions, tableColumnsCache.DocumentsModifiedByTransactionsColumns["delete_document"], false);
				Api.SetColumn(session, DocumentsModifiedByTransactions, tableColumnsCache.DocumentsModifiedByTransactionsColumns["locked_by_transaction"], transactionInformation.Id.ToByteArray());

				update.Save();
			}
			logger.Debug("Inserted a new document with key '{0}', update: {1}, in transaction: {2}",
							   key, isUpdate, transactionInformation.Id);

			return newEtag;
		}


		public bool DeleteDocument(string key, Guid? etag, out RavenJObject metadata, out Guid? deletedETag)
		{
			metadata = null;
			Api.JetSetCurrentIndex(session, Documents, "by_key");
			Api.MakeKey(session, Documents, key, Encoding.Unicode, MakeKeyGrbit.NewKey);
			if (Api.TrySeek(session, Documents, SeekGrbit.SeekEQ) == false)
			{
				logger.Debug("Document with key '{0}' was not found, and considered deleted", key);
				deletedETag = null;
				return false;
			}
			if (Api.TryMoveFirst(session, Details))
				Api.EscrowUpdate(session, Details, tableColumnsCache.DetailsColumns["document_count"], -1);

			var existingEtag = EnsureDocumentEtagMatch(key, etag, "DELETE");
			EnsureNotLockedByTransaction(key, null);

			metadata = Api.RetrieveColumn(session, Documents, tableColumnsCache.DocumentsColumns["metadata"]).ToJObject();
			deletedETag = existingEtag;

			Api.JetDelete(session, Documents);
			logger.Debug("Document with key '{0}' was deleted", key);

			cacher.RemoveCachedDocument(key, existingEtag);

			return true;
		}


		public bool DeleteDocumentInTransaction(TransactionInformation transactionInformation, string key, Guid? etag)
		{
			Api.JetSetCurrentIndex(session, Documents, "by_key");
			Api.MakeKey(session, Documents, key, Encoding.Unicode, MakeKeyGrbit.NewKey);
			if (Api.TrySeek(session, Documents, SeekGrbit.SeekEQ) == false)
			{
				if (etag != null && etag.Value != Guid.Empty)
				{
					throw new ConcurrencyException("DELETE attempted on document '" + key +
											   "' using a non current etag")
					{
						ActualETag = Guid.Empty,
						ExpectedETag = etag.Value,
                        Key = key
					};
				}
				logger.Debug("Document with key '{0}' was not found, and considered deleted", key);
				return false;
			}

			EnsureNotLockedByTransaction(key, transactionInformation.Id);
			EnsureDocumentEtagMatchInTransaction(key, etag);

			using (var update = new Update(session, Documents, JET_prep.Replace))
			{
				Api.SetColumn(session, Documents, tableColumnsCache.DocumentsColumns["locked_by_transaction"], transactionInformation.Id.ToByteArray());
				update.Save();
			}
			EnsureTransactionExists(transactionInformation);

			Guid newEtag = uuidGenerator.CreateSequentialUuid(UuidType.DocumentTransactions);

			Api.JetSetCurrentIndex(session, DocumentsModifiedByTransactions, "by_key");
			Api.MakeKey(session, DocumentsModifiedByTransactions, key, Encoding.Unicode, MakeKeyGrbit.NewKey);
			var isUpdateInTransaction = Api.TrySeek(session, DocumentsModifiedByTransactions, SeekGrbit.SeekEQ);

			using (var update = new Update(session, DocumentsModifiedByTransactions, isUpdateInTransaction ? JET_prep.Replace : JET_prep.Insert))
			{
				Api.SetColumn(session, DocumentsModifiedByTransactions, tableColumnsCache.DocumentsModifiedByTransactionsColumns["key"], key, Encoding.Unicode);
				Api.SetColumn(session, DocumentsModifiedByTransactions, tableColumnsCache.DocumentsModifiedByTransactionsColumns["data"],
					Api.RetrieveColumn(session, Documents, tableColumnsCache.DocumentsColumns["data"]));
				Api.SetColumn(session, DocumentsModifiedByTransactions,
							  tableColumnsCache.DocumentsModifiedByTransactionsColumns["etag"],
							  newEtag.TransformToValueForEsentSorting());
				Api.SetColumn(session, DocumentsModifiedByTransactions, tableColumnsCache.DocumentsModifiedByTransactionsColumns["last_modified"],
					Api.RetrieveColumnAsInt64(session, Documents, tableColumnsCache.DocumentsColumns["last_modified"]).Value);
				Api.SetColumn(session, DocumentsModifiedByTransactions, tableColumnsCache.DocumentsModifiedByTransactionsColumns["metadata"],
					Api.RetrieveColumn(session, Documents, tableColumnsCache.DocumentsColumns["metadata"]));
				Api.SetColumn(session, DocumentsModifiedByTransactions, tableColumnsCache.DocumentsModifiedByTransactionsColumns["delete_document"], true);
				Api.SetColumn(session, DocumentsModifiedByTransactions, tableColumnsCache.DocumentsModifiedByTransactionsColumns["locked_by_transaction"], transactionInformation.Id.ToByteArray());

				update.Save();
			}

			return true;
		}
	}
}

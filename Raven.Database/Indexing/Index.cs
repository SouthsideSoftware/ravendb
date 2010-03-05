using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using log4net;
using Lucene.Net.Analysis.Standard;
using Lucene.Net.Documents;
using Lucene.Net.Index;
using Lucene.Net.QueryParsers;
using Lucene.Net.Search;
using Lucene.Net.Store;
using Raven.Database.Extensions;
using Raven.Database.Linq;

namespace Raven.Database.Indexing
{
    /// <summary>
    /// This is a thread safe, single instance for a particular index.
    /// </summary>
    public class Index : IDisposable
    {
        private readonly ILog log = LogManager.GetLogger(typeof (Index));

        private class CurrentIndexSearcher
        {
            public IndexSearcher Searcher{ get; set;}
            private int useCount;
            private bool shouldDisposeWhenThereAreNoUsages;


            public IDisposable Use()
            {
                Interlocked.Increment(ref useCount);
                return new CleanUp(this);
            }

            private class CleanUp : IDisposable
            {
                private readonly CurrentIndexSearcher parent;

                public CleanUp(CurrentIndexSearcher parent)
                {
                    this.parent = parent;
                }

                public void Dispose()
                {
                    var uses = Interlocked.Decrement(ref parent.useCount);
                    if (parent.shouldDisposeWhenThereAreNoUsages && uses == 0)
                        parent.Searcher.Close();

                }
            }

            public void MarkForDispoal()
            {
                shouldDisposeWhenThereAreNoUsages = true;
            }
        }

        private readonly Directory directory;
        private CurrentIndexSearcher searcher;
        private readonly string name;

        public Index(Directory directory, string name)
        {
            this.name = name;
            log.DebugFormat("Creating index for {0}", name);
            this.directory = directory;
            searcher = new CurrentIndexSearcher
            {
                Searcher = new IndexSearcher(directory)
            };
        }

        public void Dispose()
        {
            searcher.Searcher.Close();
            directory.Close();
        }

        public IEnumerable<string> Query(string query, int start, int pageSize, Reference<int> totalSize)
        {
            using (searcher.Use())
            {
                var indexSearcher = searcher.Searcher;
                if(string.IsNullOrEmpty(query) == false)
                {
                    return SearchIndex(query, indexSearcher, totalSize, start, pageSize);
                }
                return BrowseIndex(indexSearcher, totalSize, start, pageSize);
            }
        }

        private IEnumerable<string> BrowseIndex(IndexSearcher indexSearcher, Reference<int> totalSize, int start, int pageSize)
        {
            log.DebugFormat("Browsing index {0}", name);
            var maxDoc = indexSearcher.MaxDoc();
            totalSize.Value = maxDoc;
            for (int i = start; i < maxDoc && (i - start) < pageSize; i++)
            {
                yield return indexSearcher.Doc(i).GetField("__document_id").StringValue();
            }
        }

        private IEnumerable<string> SearchIndex(string query, IndexSearcher indexSearcher, Reference<int> totalSize, int start, int pageSize)
        {
            log.DebugFormat("Issuing query on index {0} for: {1}", name, query);
            var luceneQuery = new QueryParser("", new StandardAnalyzer()).Parse(query);
            var search = indexSearcher.Search(luceneQuery);
            totalSize.Value = search.Length();
            for (int i = start; i < search.Length() && (i - start) < pageSize; i++)
            {
                yield return search.Doc(i).GetField("__document_id").StringValue();
            }
        }

        private void Write(Func<IndexWriter, bool> action)
        {
            var indexWriter = new IndexWriter(directory, new StandardAnalyzer());
            bool shouldRcreateSearcher;
            try
            {
                shouldRcreateSearcher = action(indexWriter);
            }
            finally
            {
                indexWriter.Close();
            }
            if (shouldRcreateSearcher)
                RecreateSearcher();
        }

        public void IndexDocuments(IndexingFunc func, IEnumerable<object> documents)
        {
            JsonToLuceneDocumentConverter converter = null;
            Write(indexWriter =>
            {
                var docs = func(documents);

                converter = new JsonToLuceneDocumentConverter(indexWriter);

                foreach (var doc in docs)
                {
                    converter.Index(doc);
                }
                converter.FlushDocumentIfNeeded();

                return converter.ShouldRcreateSearcher;
            });
            if (converter == null)
                return;
            log.InfoFormat("Indexed {0} documents for {1}", converter.Count, name);
        }


        private void RecreateSearcher()
        {
            using (searcher.Use())
            {
                searcher.MarkForDispoal();
                searcher = new CurrentIndexSearcher
                {
                    Searcher = new IndexSearcher(directory)
                };
                Thread.MemoryBarrier();// force other threads to see this write
            }
        }

        public void Remove(string[] keys)
        {
            Write(writer =>
            {
                if(log.IsDebugEnabled)
                {
                    log.DebugFormat("Deleting ({0}) from {1}", string.Format(", ", keys), name);
                }
                writer.DeleteDocuments(keys.Select(k => new Term("__document_id", k)).ToArray());
                return true;
            });
        }
    }
}
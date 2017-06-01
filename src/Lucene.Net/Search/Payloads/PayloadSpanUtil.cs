using System;
using System.Collections.Generic;
using System.Linq;

namespace Lucene.Net.Search.Payloads
{
    /*
     * Licensed to the Apache Software Foundation (ASF) under one or more
     * contributor license agreements.  See the NOTICE file distributed with
     * this work for additional information regarding copyright ownership.
     * The ASF licenses this file to You under the Apache License, Version 2.0
     * (the "License"); you may not use this file except in compliance with
     * the License.  You may obtain a copy of the License at
     *
     *     http://www.apache.org/licenses/LICENSE-2.0
     *
     * Unless required by applicable law or agreed to in writing, software
     * distributed under the License is distributed on an "AS IS" BASIS,
     * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
     * See the License for the specific language governing permissions and
     * limitations under the License.
     */

    using AtomicReaderContext = Lucene.Net.Index.AtomicReaderContext;
    using IndexReaderContext = Lucene.Net.Index.IndexReaderContext;
    using SpanNearQuery = Lucene.Net.Search.Spans.SpanNearQuery;
    using SpanOrQuery = Lucene.Net.Search.Spans.SpanOrQuery;
    using SpanQuery = Lucene.Net.Search.Spans.SpanQuery;
    using Spans = Lucene.Net.Search.Spans.Spans;
    using SpanTermQuery = Lucene.Net.Search.Spans.SpanTermQuery;
    using Term = Lucene.Net.Index.Term;
    using TermContext = Lucene.Net.Index.TermContext;

    /// <summary>
    /// Experimental class to get set of payloads for most standard Lucene queries.
    /// Operates like Highlighter - <see cref="Index.IndexReader"/> should only contain doc of interest,
    /// best to use MemoryIndex.
    /// <para/>
    /// @lucene.experimental
    /// </summary>
#if FEATURE_SERIALIZABLE
    [Serializable]
#endif
    public class PayloadSpanUtil
    {
        private IndexReaderContext context;

        /// <param name="context">
        ///          that contains doc with payloads to extract
        /// </param>
        /// <seealso cref="Index.IndexReader.Context"/>
        public PayloadSpanUtil(IndexReaderContext context)
        {
            this.context = context;
        }

        /// <summary>
        /// Query should be rewritten for wild/fuzzy support.
        /// </summary>
        /// <param name="query"> rewritten query </param>
        /// <returns> payloads Collection </returns>
        /// <exception cref="System.IO.IOException"> if there is a low-level I/O error </exception>
        public virtual ICollection<byte[]> GetPayloadsForQuery(Query query)
        {
            var payloads = new List<byte[]>();
            QueryToSpanQuery(query, payloads);
            return payloads;
        }

        private void QueryToSpanQuery(Query query, ICollection<byte[]> payloads)
        {
            if (query is BooleanQuery)
            {
                BooleanClause[] queryClauses = ((BooleanQuery)query).GetClauses();

                for (int i = 0; i < queryClauses.Length; i++)
                {
                    if (!queryClauses[i].IsProhibited)
                    {
                        QueryToSpanQuery(queryClauses[i].Query, payloads);
                    }
                }
            }
            else if (query is PhraseQuery)
            {
                Term[] phraseQueryTerms = ((PhraseQuery)query).GetTerms();
                SpanQuery[] clauses = new SpanQuery[phraseQueryTerms.Length];
                for (int i = 0; i < phraseQueryTerms.Length; i++)
                {
                    clauses[i] = new SpanTermQuery(phraseQueryTerms[i]);
                }

                int slop = ((PhraseQuery)query).Slop;
                bool inorder = false;

                if (slop == 0)
                {
                    inorder = true;
                }

                SpanNearQuery sp = new SpanNearQuery(clauses, slop, inorder);
                sp.Boost = query.Boost;
                GetPayloads(payloads, sp);
            }
            else if (query is TermQuery)
            {
                SpanTermQuery stq = new SpanTermQuery(((TermQuery)query).Term);
                stq.Boost = query.Boost;
                GetPayloads(payloads, stq);
            }
            else if (query is SpanQuery)
            {
                GetPayloads(payloads, (SpanQuery)query);
            }
            else if (query is FilteredQuery)
            {
                QueryToSpanQuery(((FilteredQuery)query).Query, payloads);
            }
            else if (query is DisjunctionMaxQuery)
            {
                IEnumerator<Query> enumerator = ((DisjunctionMaxQuery)query).GetEnumerator();
                while (enumerator.MoveNext())
                {
                    QueryToSpanQuery(enumerator.Current, payloads);
                }
            }
            else if (query is MultiPhraseQuery)
            {
                MultiPhraseQuery mpq = (MultiPhraseQuery)query;
                IList<Term[]> termArrays = mpq.GetTermArrays();
                int[] positions = mpq.GetPositions();
                if (positions.Length > 0)
                {
                    int maxPosition = positions[positions.Length - 1];
                    for (int i = 0; i < positions.Length - 1; ++i)
                    {
                        if (positions[i] > maxPosition)
                        {
                            maxPosition = positions[i];
                        }
                    }

                    IList<Query>[] disjunctLists = new List<Query>[maxPosition + 1];
                    int distinctPositions = 0;

                    for (int i = 0; i < termArrays.Count; ++i)
                    {
                        Term[] termArray = termArrays[i];
                        IList<Query> disjuncts = disjunctLists[positions[i]];
                        if (disjuncts == null)
                        {
                            disjuncts = (disjunctLists[positions[i]] = new List<Query>(termArray.Length));
                            ++distinctPositions;
                        }
                        foreach (Term term in termArray)
                        {
                            disjuncts.Add(new SpanTermQuery(term));
                        }
                    }

                    int positionGaps = 0;
                    int position = 0;
                    SpanQuery[] clauses = new SpanQuery[distinctPositions];
                    for (int i = 0; i < disjunctLists.Length; ++i)
                    {
                        IList<Query> disjuncts = disjunctLists[i];
                        if (disjuncts != null)
                        {
                            clauses[position++] = new SpanOrQuery(disjuncts.OfType<SpanQuery>().ToArray());
                        }
                        else
                        {
                            ++positionGaps;
                        }
                    }

                    int slop = mpq.Slop;
                    bool inorder = (slop == 0);

                    SpanNearQuery sp = new SpanNearQuery(clauses, slop + positionGaps, inorder);
                    sp.Boost = query.Boost;
                    GetPayloads(payloads, sp);
                }
            }
        }

        private void GetPayloads(ICollection<byte[]> payloads, SpanQuery query)
        {
            IDictionary<Term, TermContext> termContexts = new Dictionary<Term, TermContext>();
            var terms = new SortedSet<Term>();
            query.ExtractTerms(terms);
            foreach (Term term in terms)
            {
                termContexts[term] = TermContext.Build(context, term);
            }
            foreach (AtomicReaderContext atomicReaderContext in context.Leaves)
            {
                Spans spans = query.GetSpans(atomicReaderContext, atomicReaderContext.AtomicReader.LiveDocs, termContexts);
                while (spans.Next() == true)
                {
                    if (spans.IsPayloadAvailable)
                    {
                        var payload = spans.GetPayload();
                        foreach (var bytes in payload)
                        {
                            payloads.Add(bytes);
                        }
                    }
                }
            }
        }
    }
}
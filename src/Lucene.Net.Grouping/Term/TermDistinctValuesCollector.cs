﻿using Lucene.Net.Index;
using Lucene.Net.Support;
using Lucene.Net.Util;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Lucene.Net.Search.Grouping.Terms
{
    /// <summary>
    /// A term based implementation of <see cref="AbstractDistinctValuesCollector{TermDistinctValuesCollector.GroupCount}"/> that relies
    /// on <see cref="SortedDocValues"/> to count the distinct values per group.
    /// 
    /// @lucene.experimental
    /// </summary>
    public class TermDistinctValuesCollector : AbstractDistinctValuesCollector<TermDistinctValuesCollector.GroupCount>
    {
        private readonly string groupField;
        private readonly string countField;
        private readonly List<GroupCount> groups;
        private readonly SentinelIntSet ordSet;
        private readonly GroupCount[] groupCounts;

        private SortedDocValues groupFieldTermIndex;
        private SortedDocValues countFieldTermIndex;

        /**
         * Constructs {@link TermDistinctValuesCollector} instance.
         *
         * @param groupField The field to group by
         * @param countField The field to count distinct values for
         * @param groups The top N groups, collected during the first phase search
         */
        public TermDistinctValuesCollector(string groupField, string countField, IEnumerable<ISearchGroup<BytesRef>> groups)
        {
            this.groupField = groupField;
            this.countField = countField;
            int groupCount = groups.Count();
            this.groups = new List<GroupCount>(groupCount);
            foreach (ISearchGroup<BytesRef> group in groups)
            {
                this.groups.Add(new GroupCount(group.GroupValue));
            }
            ordSet = new SentinelIntSet(groupCount, -2);
            groupCounts = new GroupCount[ordSet.Keys.Length];
        }

        public override void Collect(int doc)
        {
            int slot = ordSet.Find(groupFieldTermIndex.GetOrd(doc));
            if (slot < 0)
            {
                return;
            }

            GroupCount gc = groupCounts[slot];
            int countOrd = countFieldTermIndex.GetOrd(doc);
            if (DoesNotContainOrd(countOrd, gc.ords))
            {
                if (countOrd == -1)
                {
                    ((ISet<BytesRef>)gc.UniqueValues).Add(null);
                }
                else
                {
                    BytesRef br = new BytesRef();
                    countFieldTermIndex.LookupOrd(countOrd, br);
                    ((ISet<BytesRef>)gc.UniqueValues).Add(br);
                }

                gc.ords = Arrays.CopyOf(gc.ords, gc.ords.Length + 1);
                gc.ords[gc.ords.Length - 1] = countOrd;
                if (gc.ords.Length > 1)
                {
                    Array.Sort(gc.ords);
                }
            }
        }

        private bool DoesNotContainOrd(int ord, int[] ords)
        {
            if (ords.Length == 0)
            {
                return true;
            }
            else if (ords.Length == 1)
            {
                return ord != ords[0];
            }
            return Array.BinarySearch(ords, ord) < 0;
        }

        public override IEnumerable<GroupCount> Groups
        {
            get { return groups; }
        }

        public override AtomicReaderContext NextReader
        {
            set
            {
                groupFieldTermIndex = FieldCache.DEFAULT.GetTermsIndex(value.AtomicReader, groupField);
                countFieldTermIndex = FieldCache.DEFAULT.GetTermsIndex(value.AtomicReader, countField);
                ordSet.Clear();
                foreach (GroupCount group in groups)
                {
                    int groupOrd = group.GroupValue == null ? -1 : groupFieldTermIndex.LookupTerm(group.GroupValue);
                    if (group.GroupValue != null && groupOrd < 0)
                    {
                        continue;
                    }

                    groupCounts[ordSet.Put(groupOrd)] = group;
                    group.ords = new int[group.UniqueValues.Count()];
                    Arrays.Fill(group.ords, -2);
                    int i = 0;
                    foreach (BytesRef value2 in group.UniqueValues)
                    {
                        int countOrd = value2 == null ? -1 : countFieldTermIndex.LookupTerm(value2);
                        if (value2 == null || countOrd >= 0)
                        {
                            group.ords[i++] = countOrd;
                        }
                    }
                }
            }
        }

        /** Holds distinct values for a single group.
         *
         * @lucene.experimental */
        public class GroupCount : AbstractGroupCount<BytesRef> /*AbstractDistinctValuesCollector.GroupCount<BytesRef>*/
        {
            internal int[] ords;

            internal GroupCount(BytesRef groupValue)
                    : base(groupValue)
            {
            }
        }
    }
}

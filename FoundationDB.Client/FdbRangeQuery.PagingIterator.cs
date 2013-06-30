﻿#region BSD Licence
/* Copyright (c) 2013, Doxense SARL
All rights reserved.

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are met:
	* Redistributions of source code must retain the above copyright
	  notice, this list of conditions and the following disclaimer.
	* Redistributions in binary form must reproduce the above copyright
	  notice, this list of conditions and the following disclaimer in the
	  documentation and/or other materials provided with the distribution.
	* Neither the name of Doxense nor the
	  names of its contributors may be used to endorse or promote products
	  derived from this software without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND
ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL <COPYRIGHT HOLDER> BE LIABLE FOR ANY
DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
(INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
(INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */
#endregion

//enable this to enable verbose traces when doing paging
#undef DEBUG_RANGE_PAGING

namespace FoundationDB.Client
{
	using FoundationDB.Client.Native;
	using FoundationDB.Client.Utils;
	using FoundationDB.Linq;
	using System;
	using System.Collections.Generic;
	using System.Diagnostics;
	using System.Threading;
	using System.Threading.Tasks;

	public partial class FdbRangeQuery
	{

		/// <summary>Async iterator that fetches the results by batch, but return them one by one</summary>
		/// <typeparam name="TResult">Type of the results returned</typeparam>
		[DebuggerDisplay("State={m_state}, Iteration={Iteration}, AtEnd={AtEnd}, HasMore={HasMore}")]
		private sealed class PagingIterator : FdbAsyncEnumerable.AsyncIterator<KeyValuePair<Slice, Slice>[]>
		{

			#region Iterable Properties...

			private FdbRangeQuery Query { get; set; }

			private FdbTransaction Transaction { get; set; }

			#endregion

			#region Iterator Properties...

			/// <summary>Key selector describing the beginning of the current range (when paging)</summary>
			private FdbKeySelector Begin { get; set; }

			/// <summary>Key selector describing the end of the current range (when paging)</summary>
			private FdbKeySelector End { get; set; }

			/// <summary>If non null, contains the remaining allowed number of rows</summary>
			private int? Remaining { get; set; }

			/// <summary>Iteration number of current page (in iterator mode)</summary>
			private int Iteration { get; set; }

			/// <summary>Current page (may contain all records or only a segment at a time)</summary>
			private KeyValuePair<Slice, Slice>[] Chunk { get; set; }

			/// <summary>If true, we have more records pending</summary>
			private bool HasMore { get; set; }

			/// <summary>True if we have reached the last page</summary>
			private bool AtEnd { get; set; }

			/// <summary>Running total of rows that have been read</summary>
			private int RowCount { get; set; }

			/// <summary>Current/Last batch read task</summary>
			private Task<bool> PendingReadTask { get; set; }

			#endregion

			public PagingIterator(FdbRangeQuery query, FdbTransaction transaction)
			{
				Contract.Requires(query != null);

				this.Query = query;
				this.Transaction = transaction ?? query.Transaction;
			}

			protected override FdbAsyncEnumerable.AsyncIterator<KeyValuePair<Slice, Slice>[]> Clone()
			{
				return new PagingIterator(this.Query, this.Transaction);
			}

			#region IFdbAsyncEnumerator<T>...

			protected override Task<bool> OnFirstAsync(CancellationToken ct)
			{
				this.Remaining = this.Query.Limit > 0 ? this.Query.Limit : default(int?);
				this.Begin = this.Query.Range.Start;
				this.End = this.Query.Range.Stop;

				return TaskHelpers.TrueTask;
			}

			protected override Task<bool> OnNextAsync(CancellationToken cancellationToken)
			{
				// Make sure that we are not called while the previous fetch is still running
				if (this.PendingReadTask != null && !this.PendingReadTask.IsCompleted)
				{
					throw new InvalidOperationException("Cannot fetch another page while a previous read operation is still pending");
				}

				if (this.AtEnd)
				{ // we already read the last batch !
					return TaskHelpers.FromResult(Completed());
				}

				// slower path, we need to actually read the first batch...
				return FetchNextPageAsync(cancellationToken);
			}

			/// <summary>Asynchronously fetch a new page of results</summary>
			/// <param name="ct"></param>
			/// <returns>True if Chunk contains a new page of results. False if all results have been read.</returns>
			private Task<bool> FetchNextPageAsync(CancellationToken ct)
			{
				Contract.Requires(!this.AtEnd);
				Contract.Requires(this.Iteration >= 0);

				this.Transaction.EnsuresCanReadOrWrite(ct);

				this.Iteration++;

#if DEBUG_RANGE_PAGING
				Debug.WriteLine("FdbRangeQuery.PagingIterator.FetchNextPageAsync(iter=" + this.Iteration + ") started");
#endif

				var future = FdbNative.TransactionGetRange(this.Transaction.Handle, this.Begin, this.End, this.Remaining.GetValueOrDefault(), this.Query.TargetBytes, this.Query.Mode, this.Iteration, this.Query.Snapshot, this.Query.Reverse);
				var task = FdbFuture.CreateTaskFromHandle(
					future,
					(h) =>
					{
						if (this.Transaction == null)
						{ // disposed ? quietly return
							return false;
						}

						//TODO: locking ?

						bool hasMore;
						var chunk = GetKeyValueArrayResult(h, out hasMore);
						this.Chunk = chunk;
						this.RowCount += chunk.Length;
						this.HasMore = hasMore;
						// subtract number of row from the remaining allowed
						if (this.Remaining.HasValue) this.Remaining = this.Remaining.Value - chunk.Length;

						this.AtEnd = !hasMore || (this.Remaining.HasValue && this.Remaining.Value <= 0);

						if (!this.AtEnd)
						{ // update begin..end so that next call will continue from where we left...
							var lastKey = chunk[chunk.Length - 1].Key;
							if (this.Query.Reverse)
							{
								this.End = FdbKeySelector.FirstGreaterOrEqual(lastKey);
							}
							else
							{
								this.Begin = FdbKeySelector.FirstGreaterThan(lastKey);
							}
						}
#if DEBUG_RANGE_PAGING
						Debug.WriteLine("FdbRangeQuery.PagingIterator.FetchNextPageAsync() returned " + this.Chunk.Length + " results (" + this.RowCount + " total) " + (hasMore ? " with more to come" : " and has no more data"));
#endif
						if (chunk.Length > 0 && this.Transaction != null)
						{
							return Publish(chunk);
						}
						return Completed();
					},
					ct
				);

				// keep track of this operation
				this.PendingReadTask = task;
				return task;
			}

			/// <summary>Extract a chunk of result from a completed Future</summary>
			/// <param name="h">Handle to the completed Future</param>
			/// <param name="more">Receives true if there are more result, or false if all results have been transmited</param>
			/// <returns></returns>
			private static KeyValuePair<Slice, Slice>[] GetKeyValueArrayResult(FutureHandle h, out bool more)
			{
				KeyValuePair<Slice, Slice>[] result;
				var err = FdbNative.FutureGetKeyValueArray(h, out result, out more);
#if DEBUG_RANGE_PAGING
				Debug.WriteLine("FdbRangeQuery.PagingIterator.GetKeyValueArrayResult() => err=" + err + ", result=" + (result != null ? result.Length : -1) + ", more=" + more);
#endif
				Fdb.DieOnError(err);
				return result;
			}

			#endregion

			protected override void Cleanup()
			{
				//TODO: should we wait/cancel any pending read task ?
				this.Chunk = null;
				this.AtEnd = true;
				this.HasMore = false;
				this.Remaining = null;
				this.Iteration = -1;
				this.PendingReadTask = null;
			}
		}

	}

}
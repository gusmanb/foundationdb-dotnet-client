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

namespace FoundationDB.Client
{
	using JetBrains.Annotations;
	using System;
	using System.Collections.Generic;
	using System.Globalization;
	using System.Linq;
	using System.Net;

	/// <summary>Class that exposes the content of a FoundationDB .cluster file</summary>
	public sealed class FdbClusterFile
	{
		/// <summary>The raw value of the file</summary>
		internal string RawValue { [NotNull] get; private set; }

		/// <summary>Cluster Identifier</summary>
		public string Id { [NotNull] get; private set; }

		/// <summary>Logical description of the database</summary>
		public string Description { [NotNull] get; private set; }

		/// <summary>List of coordination servers</summary>
		public FdbEndPoint[] Coordinators { get; private set; }

		private FdbClusterFile()
		{ }

		/// <summary>Cluster file with already parsed components</summary>
		/// <param name="description"></param>
		/// <param name="identifier"></param>
		/// <param name="coordinators"></param>
		public FdbClusterFile(string description, string identifier, IEnumerable<FdbEndPoint> coordinators)
		{
			if (description == null) throw new ArgumentNullException("description");
			if (identifier == null) throw new ArgumentNullException("identifier");
			if (coordinators == null) throw new ArgumentNullException("coordinators");

			this.Description = description;
			this.Id = identifier;
			this.Coordinators = coordinators.ToArray(); // create a copy of the array

			this.RawValue = String.Format(
				CultureInfo.InvariantCulture,
				"{0}:{1}@{2}",
				this.Description,
				this.Id,
				String.Join(",", this.Coordinators.Select(kvp => String.Format(CultureInfo.InvariantCulture, "{0}:{1}", kvp.Address, kvp.Port)))
			);
		}

		/// <summary>Parse the content of a .cluster file</summary>
		/// <param name="rawValue">First line of a .cluster file</param>
		/// <returns>Parsed cluster file instance</returns>
		[NotNull]
		public static FdbClusterFile Parse(string rawValue)
		{
			if (string.IsNullOrEmpty(rawValue)) throw new FormatException("Cluster file descriptor cannot be empty");

			int p = rawValue.IndexOf(':');
			if (p < 0) throw new FormatException("Missing ':' after description field");
			string description = rawValue.Substring(0, p).Trim();
			if (description.Length == 0) throw new FormatException("Empty description field");

			int q  = rawValue.IndexOf('@', p + 1);
			if (q < 0) throw new FormatException("Missing '@' after identifier field");
			string identifier = rawValue.Substring(p + 1, q - p - 1).Trim();
			if (identifier.Length == 0) throw new FormatException("Empty description field");

			string[] pairs = rawValue.Substring(q + 1).Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
			var coordinators = pairs.Select(pair =>
			{
				bool tls = false;
				if (pair.EndsWith(":tls", StringComparison.OrdinalIgnoreCase))
				{
					pair = pair.Substring(0, pair.Length - 4);
					tls = true;
				}
				int r = pair.LastIndexOf(':');
				if (r < 0) throw new FormatException("Missing ':' in coordinator address");
				// the format is "{IP}:{PORT}" or "{IP}:{PORT}:tls"

				return new FdbEndPoint(
					IPAddress.Parse(pair.Substring(0, r)),
					Int32.Parse(pair.Substring(r + 1)),
					tls
				);
			}).ToArray();
			if (coordinators.Length == 0) throw new FormatException("Empty coordination server list");

			return new FdbClusterFile
			{
				RawValue = rawValue,
				Description = description,
				Id = identifier,
				Coordinators = coordinators
			};

		}

		/// <summary>Returns the raw text of the cluster file</summary>
		/// <returns></returns>
		public override string ToString()
		{
			return this.RawValue;
		}

		/// <summary>Computes the hashcode of the cluster file</summary>
		public override int GetHashCode()
		{
			if (this.RawValue == null) return -1;
			return this.RawValue.GetHashCode();
		}

		/// <summary>Check if this cluster file is equal to another object</summary>
		public override bool Equals(object obj)
		{
			var cf = obj as FdbClusterFile;
			return cf != null && string.Equals(this.RawValue, cf.RawValue, StringComparison.Ordinal);
		}

	}

}

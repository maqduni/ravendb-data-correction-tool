﻿using System.Collections.Generic;
using System.Linq;
using System.Net.Http.Headers;

namespace Raven.Client.Connection
{
	public static class HttpResponseHeadersExtensions
	{
		/// <returns>
		/// Returns <see cref="T:System.Collections.Generic.IEnumerable`1"/>.
		/// </returns>
		public static string GetFirstValue(this HttpResponseHeaders headers, string name)
		{
			IEnumerable<string> values;
			if (!headers.TryGetValues(name, out values))
				return null;
			return values.FirstOrDefault();
		}
	}
}
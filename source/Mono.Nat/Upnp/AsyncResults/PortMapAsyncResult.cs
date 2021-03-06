//
// Authors:
//   Alan McGovern alan.mcgovern@gmail.com
//
// Copyright (C) 2006 Alan McGovern
//
// Permission is hereby granted, free of charge, to any person obtaining
// a copy of this software and associated documentation files (the
// "Software"), to deal in the Software without restriction, including
// without limitation the rights to use, copy, modify, merge, publish,
// distribute, sublicense, and/or sell copies of the Software, and to
// permit persons to whom the Software is furnished to do so, subject to
// the following conditions:
// 
// The above copyright notice and this permission notice shall be
// included in all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
// MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
// LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
// OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
// WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
//


using System;
using System.Net;

namespace Mono.Nat.Upnp
{
	internal class PortMapAsyncResult : AsyncResult
	{
		protected PortMapAsyncResult(WebRequest request, AsyncCallback callback, object asyncState)
			: base(callback, asyncState)
		{
			this.Request = request;
		}

		internal WebRequest Request { get; set; }

		internal MessageBase SavedMessage { get; set; }

		internal static PortMapAsyncResult Create(MessageBase message, WebRequest request, AsyncCallback storedCallback,
			object asyncState)
		{
			if (message is GetGenericPortMappingEntry)
				return new GetAllMappingsAsyncResult(request, storedCallback, asyncState);

			if (message is GetSpecificPortMappingEntryMessage)
			{
				var mapMessage = (GetSpecificPortMappingEntryMessage) message;
				var result = new GetAllMappingsAsyncResult(request, storedCallback, asyncState);

				result.SpecificMapping = new Mapping(mapMessage.protocol, 0, mapMessage.externalPort, 0);
				return result;
			}

			return new PortMapAsyncResult(request, storedCallback, asyncState);
		}
	}
}
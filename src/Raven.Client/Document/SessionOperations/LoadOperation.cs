using System;
using System.Diagnostics;
using Raven.Abstractions.Logging;
using Raven.Abstractions.Data;

namespace Raven.Client.Document.SessionOperations
{
    public class LoadOperation
    {
        private readonly static ILog log = LogManager.GetLogger(typeof(LoadOperation));

        protected readonly InMemoryDocumentSessionOperations sessionOperations;
        private readonly Func<IDisposable> disableAllCaching;
        private readonly string id;
        private bool firstRequest = true;
        private JsonDocument documentFound;

        public LoadOperation(InMemoryDocumentSessionOperations sessionOperations, Func<IDisposable> disableAllCaching, string id)
        {
            if (id == null) throw new ArgumentNullException("id", "The document id cannot be null");
            this.sessionOperations = sessionOperations;
            this.disableAllCaching = disableAllCaching;
            this.id = id;
        }

        public void LogOperation()
        {
            if (log.IsDebugEnabled)
                log.Debug("Loading document [{0}] from {1}", id, sessionOperations.StoreIdentifier);
            }			

        public IDisposable EnterLoadContext()
        {
            if (firstRequest == false) // if this is a repeated request, we mustn't use the cached result, but have to re-query the server
                return disableAllCaching();

            return null;
        }

        public bool SetResult(JsonDocument document)
        {
            firstRequest = false;
            documentFound = document;

                return false;
        }

        public virtual T Complete<T>()
        {
            if (documentFound == null)
            {
                sessionOperations.RegisterMissing(id);
                return default(T);
            }
            return sessionOperations.TrackEntity<T>(documentFound);
        }
    }
}
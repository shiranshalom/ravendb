using System;
using System.Diagnostics;

namespace Voron.Exceptions
{
    public class RecoverableFailureNotification
    {
        private readonly Action<string, Guid, string, Exception> _recoverableFailure;

        public RecoverableFailureNotification(Action<string, Guid, string, Exception> recoverableFailureHandler)
        {
            Debug.Assert(recoverableFailureHandler != null);

            _recoverableFailure = recoverableFailureHandler;
        }

        public void RaiseNotification(string failureMessage, Guid environmentId, string environmentPath, Exception e)
        {
            _recoverableFailure.Invoke(failureMessage, environmentId, environmentPath, e);
        }
    }
}

﻿using System.Xml;
using NLog.Config;
using Raven.Database.Server;

namespace Raven.Tests
{
	public class WithNLog
	{
		static WithNLog()
		{
			if (NLog.LogManager.Configuration != null)
				return;

			HttpEndpointRegistration.RegisterHttpEndpointTarget();

			using (var stream = typeof(RemoteClientTest).Assembly.GetManifestResourceStream("Raven.Tests.DefaultLogging.config"))
			using (var reader = XmlReader.Create(stream))
			{
				NLog.LogManager.Configuration = new XmlLoggingConfiguration(reader, "default-config");
			}
		}
	}
}
using System;
using System.IO;
using System.Reflection;
using WixToolset.Dtf.WindowsInstaller;

namespace RemoteRelay.Installer.CustomActions
{
    public static class ConfigWriter
    {
        [CustomAction]
        public static ActionResult WriteServerConfig(Session session)
        {
            try
            {
                var data = session.CustomActionData;
                var folder = data["InstallFolder"];
                var path = Path.Combine(folder, "config.json");

                if (File.Exists(path))
                {
                    session.Log("config.json already exists, preserving: {0}", path);
                    return ActionResult.Success;
                }

                var template = LoadTemplate("RemoteRelay.Installer.CustomActions.Templates.config.json.template");
                var rendered = template
                    .Replace("{{SERVER_PORT}}", NumericOrDefault(data, "ServerPort", "33101"))
                    .Replace("{{RELAY_DRIVER}}", JsonEscape(ValueOrDefault(data, "RelayDriver", "Auto")))
                    .Replace("{{K8090_PORT}}", JsonEscape(ValueOrDefault(data, "K8090Port", "")))
                    .Replace("{{LOGO_FILE}}", JsonEscape(ValueOrDefault(data, "LogoFile", "")));

                Directory.CreateDirectory(folder);
                File.WriteAllText(path, rendered);
                session.Log("Wrote server config.json to {0}", path);
                return ActionResult.Success;
            }
            catch (Exception ex)
            {
                session.Log("WriteServerConfig failed: {0}", ex);
                return ActionResult.Failure;
            }
        }

        [CustomAction]
        public static ActionResult WriteClientConfig(Session session)
        {
            try
            {
                var data = session.CustomActionData;
                var folder = data["InstallFolder"];
                var path = Path.Combine(folder, "ClientConfig.json");

                if (File.Exists(path))
                {
                    session.Log("ClientConfig.json already exists, preserving: {0}", path);
                    return ActionResult.Success;
                }

                var template = LoadTemplate("RemoteRelay.Installer.CustomActions.Templates.ClientConfig.json.template");
                var rendered = template
                    .Replace("{{CLIENT_HOST}}", JsonEscape(ValueOrDefault(data, "ClientHost", "localhost")))
                    .Replace("{{CLIENT_PORT}}", NumericOrDefault(data, "ClientPort", "33101"));

                Directory.CreateDirectory(folder);
                File.WriteAllText(path, rendered);
                session.Log("Wrote ClientConfig.json to {0}", path);
                return ActionResult.Success;
            }
            catch (Exception ex)
            {
                session.Log("WriteClientConfig failed: {0}", ex);
                return ActionResult.Failure;
            }
        }

        private static string LoadTemplate(string resourceName)
        {
            var assembly = Assembly.GetExecutingAssembly();
            using (var stream = assembly.GetManifestResourceStream(resourceName))
            {
                if (stream == null)
                {
                    throw new InvalidOperationException("Embedded template not found: " + resourceName);
                }
                using (var reader = new StreamReader(stream))
                {
                    return reader.ReadToEnd();
                }
            }
        }

        private static string ValueOrDefault(CustomActionData data, string key, string fallback)
        {
            if (data.ContainsKey(key) && !string.IsNullOrEmpty(data[key]))
            {
                return data[key];
            }
            return fallback;
        }

        private static string NumericOrDefault(CustomActionData data, string key, string fallback)
        {
            if (data.ContainsKey(key) && int.TryParse(data[key], out var n) && n > 0)
            {
                return n.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            return fallback;
        }

        private static string JsonEscape(string value)
        {
            if (value == null) return string.Empty;
            var sb = new System.Text.StringBuilder(value.Length);
            foreach (var c in value)
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"':  sb.Append("\\\""); break;
                    case '\b': sb.Append("\\b");  break;
                    case '\f': sb.Append("\\f");  break;
                    case '\n': sb.Append("\\n");  break;
                    case '\r': sb.Append("\\r");  break;
                    case '\t': sb.Append("\\t");  break;
                    default:
                        if (c < 0x20)
                            sb.AppendFormat(System.Globalization.CultureInfo.InvariantCulture, "\\u{0:X4}", (int)c);
                        else
                            sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }
    }
}

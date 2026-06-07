using System;
using System.IO.Ports;
using System.Linq;
using WixToolset.Dtf.WindowsInstaller;

namespace RemoteRelay.Installer.CustomActions
{
    public static class ComPortEnumerator
    {
        /// <summary>
        /// Immediate CA. Populates the ComboBox table for the K8090_PORT property
        /// with the COM ports currently present on the machine. If none are found,
        /// adds a single "(none detected)" entry the user can edit by typing.
        /// Sets K8090_PORT to the first detected COM if not already set.
        /// </summary>
        [CustomAction]
        public static ActionResult PopulateComPorts(Session session)
        {
            try
            {
                var ports = SerialPort.GetPortNames()
                                      .Distinct(StringComparer.OrdinalIgnoreCase)
                                      .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                                      .ToList();

                var view = session.Database.OpenView(
                    "DELETE FROM `ComboBox` WHERE `Property`='K8090_PORT'");
                view.Execute();
                view.Close();

                view = session.Database.OpenView(
                    "INSERT INTO `ComboBox` (`Property`,`Order`,`Value`,`Text`) VALUES ('K8090_PORT', ?, ?, ?) TEMPORARY");

                int order = 1;
                if (ports.Count == 0)
                {
                    using (var rec = new Record(3))
                    {
                        rec.SetInteger(1, order);
                        rec.SetString(2, "");
                        rec.SetString(3, "(no serial ports detected)");
                        view.Execute(rec);
                    }
                }
                else
                {
                    foreach (var p in ports)
                    {
                        using (var rec = new Record(3))
                        {
                            rec.SetInteger(1, order++);
                            rec.SetString(2, p);
                            rec.SetString(3, p);
                            view.Execute(rec);
                        }
                    }

                    if (string.IsNullOrEmpty(session["K8090_PORT"]))
                    {
                        session["K8090_PORT"] = ports[0];
                    }
                }

                view.Close();
                return ActionResult.Success;
            }
            catch (Exception ex)
            {
                session.Log("PopulateComPorts failed: {0}", ex);
                // Don't fail the install just because we couldn't enumerate ports.
                return ActionResult.Success;
            }
        }
    }
}

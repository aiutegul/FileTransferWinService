using System;
using System.Data;
using System.IO;
using System.Linq;
using System.Net;
using System.ServiceProcess;
using System.Timers;
using System.Configuration;
using System.Threading;
using System.Xml;
using FileTransferWindowsService.Models;
using Oracle.ManagedDataAccess.Client;

namespace FileTransferWindowsService
{
    public partial class Service1 : ServiceBase
    {
        System.Timers.Timer timer = new System.Timers.Timer();
        private OracleConnection traxConnection;
        private OracleCommand cmd;
        private DateTime last_exec_time;
        private int exec_time;
        public Service1()
        {
            InitializeComponent();
        }

        protected override void OnStart(string[] args)
        {
            WriteToFile("Service is started at " + DateTime.Now);
            openConnections();
            timer.Elapsed += new ElapsedEventHandler(OnElapsedTime);
            timer.Interval = 15000; //number in milisecinds  
            timer.Enabled = true;
        }

        private void OnElapsedTime(object sender, ElapsedEventArgs e)
        {
            using (var _context = new streamlogEntities())
            {
                exec_time = _context.TRAXPILT_SET.Select(i => i.EX_TIME).First().GetValueOrDefault();
                last_exec_time = _context.TRAXPILT_SET.Select(i => i.LAST_EXECUTION_TIME).First();
            }
                
            TimeSpan timeSpan = DateTime.Now - last_exec_time;

            if (timeSpan.TotalMilliseconds > exec_time)
            {
                PutWorkPackage();
                update_exec_times();
            }
            else
            {
                WriteToFile("Not enough time passed " + DateTime.Now);
            }
        }

        protected override void OnStop()
        {
            WriteToFile("Service is stopped at " + DateTime.Now);
            closeConnections();
        }

        //define the method that sends xml payload to the web service
        public void PutWorkPackage()
        {
            cmd = new OracleCommand();
            cmd.Connection = traxConnection;
            cmd.CommandText = "select * from KC_TRAX_2_STREAM_XML_SOURCE where created_date >= sysdate - " 
                + ConfigurationManager.AppSettings["numLastDays"];
            cmd.CommandType = CommandType.Text;
            OracleDataReader dataReader = cmd.ExecuteReader();
            if (!dataReader.HasRows)
            {
                WriteToFile("No rows were selected");
                throw new Exception("No rows selected");
            }
            timer.Enabled = false; //disable timer to be able to send data to the service in a loop

            while(dataReader.Read())
            {
                string xmlContent = dataReader.GetOracleClob(0).Value;
                var request = (HttpWebRequest)WebRequest.Create(ConfigurationManager.AppSettings["stream_url"]);
                request.Method = "PUT";
                request.ContentType = "application/xml";
                string header_key = ConfigurationManager.AppSettings["header_key"];
                string GUID = ConfigurationManager.AppSettings["header_GUID"];
                request.Headers.Add(header_key, GUID);
                request.Accept = "application/xml";
                byte[] bytes;
                bytes = System.Text.Encoding.ASCII.GetBytes(xmlContent);
                request.ContentLength = bytes.Length;

                XmlDocument xmlDoc = new XmlDocument();
                xmlDoc.LoadXml(xmlContent);

                string xpath = "ScheduledWorkPackageDto/Barcode";
                var node = xmlDoc.SelectSingleNode(xpath);
                string barcode = node.InnerText;

                //sending xml payload to service
                try
                {
                    using (Stream requestStream = request.GetRequestStream())
                    {
                        requestStream.Write(bytes, 0, bytes.Length);
                    }

                    //get response, check log file for success.

                    using (var response = ((HttpWebResponse)request.GetResponse()))
                    {
                        using (StreamReader sr = new StreamReader(response.GetResponseStream()))
                        {
                            string responseString = sr.ReadToEnd();
                            WriteLog(xmlContent, responseString, 2);
                        }
                        if (response.StatusCode == HttpStatusCode.OK)
                        {
                            WriteToFile("Sending ScheduledWorkPackageDto with barcode: " + barcode);
                            WriteToFile("Success sending ScheduledWorkPackageDto with barcode:" + barcode);
                            WriteToFile("\n");
                        }
                    }

                }

                // This is where the error response from the service is caught
                catch (WebException ex)
                {
                    string response;
                    using (StreamReader sr = new StreamReader(ex.Response.GetResponseStream()))
                    {
                        if (((HttpWebResponse)ex.Response).StatusCode == HttpStatusCode.BadRequest)
                        {
                            response = sr.ReadToEnd();
                        }
                        else
                        {
                            response = ex.Message.ToString();
                        }

                        WriteLog(xmlContent, response, 1);
                        WriteToFile("Sending ScheduledWorkPackageDto with barcode: " + barcode);
                        WriteToFile("STREAM Response StatusCode: " + ((HttpWebResponse)ex.Response).StatusCode.ToString());
                        WriteToFile("STREAM Response: " + response);
                        WriteToFile("\n");
                    }
                    
                }
                Thread.Sleep(5000); //give each iteration time to execute

            }

            timer.Enabled = true;  // enable timer again for periodicity
        }


        // Write to the log database in oracle, calling oracle stored procedure for inserting values
        public void WriteLog(string xmlSource, string response, int status)
        {
            try
            {
                using (var _context = new streamlogEntities())
                {
                    TRAXPILT_LOG logEntry = new TRAXPILT_LOG
                    {
                        XML_SOURCE = xmlSource,
                        XML_RESPONSE = response,
                        STATUS = status,
                        CREATE_DATE = DateTime.Now
                    };
                    _context.TRAXPILT_LOG.Add(logEntry);
                    _context.SaveChanges();
                }                
            }
            catch(Exception ex)
            {
                WriteToFile("Could not write to log " + ex.ToString());
            }

        }

        // after putting the workpackage to STREAM web service, update last execution time using the function below:
        public void update_exec_times()
        {
            try
            {
                using (var _context = new streamlogEntities())
                {
                    var logSetting = _context.TRAXPILT_SET.FirstOrDefault(i => i.ID == 1);
                    logSetting.LAST_EXECUTION_TIME = DateTime.Now;
                    _context.SaveChanges();
                }
                
            }
            catch (Exception ex)
            {
                WriteToFile("Could not update exec times" + ex.Message);
            }
        }

        public void openConnections()
        {
            try
            {
                traxConnection = new OracleConnection(ConfigurationManager.AppSettings["traxConnectionString"]);
                traxConnection.Open();
            }
            catch(Exception ex)
            {
                WriteToFile("Could not open connections " + ex.Message + "\n" + ex.StackTrace+ "\nInner:\n"+ ex.InnerException?.Message+"\n"+ ex.InnerException?.StackTrace);
            }
        }

        public void closeConnections()
        {
            try
            {
                traxConnection.Close();
            }
            catch(Exception ex)
            {
                WriteToFile("Could not close connections " + ex.Message);
            }
        }

        //local log file used for debugging the service
        public void WriteToFile(string Message)
        {
            string path = AppDomain.CurrentDomain.BaseDirectory + "\\Logs";
            if (!Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
            }
            string filepath = AppDomain.CurrentDomain.BaseDirectory + "\\Logs\\ServiceLog_" + DateTime.Now.Date.ToShortDateString().Replace('/', '_') + ".txt";
            if (!File.Exists(filepath))
            {
                // Create a file to write to.   
                using (StreamWriter sw = File.CreateText(filepath))
                {
                    sw.WriteLine(Message);
                }
            }
            else
            {
                using (StreamWriter sw = File.AppendText(filepath))
                {
                    sw.WriteLine(Message);
                }
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.ServiceProcess;
using System.Text;
using System.Threading.Tasks;
using System.Timers;
using System.Configuration;
using Oracle.DataAccess.Client;
using Oracle.DataAccess.Types;
using System.Data.SqlClient;
using System.Threading;
using Newtonsoft.Json;
using System.Xml;

namespace FileTransferWindowsService
{
    public partial class Service1 : ServiceBase
    {
        System.Timers.Timer timer = new System.Timers.Timer();
        private OracleConnection traxConnection;
        private OracleConnection kcConnection;
        private OracleCommand cmd;
        private DateTime last_exec_time;
        private decimal exec_time;
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
            extract_exec_times();
            TimeSpan timeSpan = DateTime.Now - last_exec_time;

            if ((decimal)timeSpan.TotalMilliseconds > exec_time)
            {
                PutWorkPackage();
                update_exec_times(DateTime.Now);
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
            cmd.CommandText = "select * from KC_TRAX_2_STREAM_XML_SOURCE where wo = 173388";
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
                var request = (HttpWebRequest)WebRequest.Create("https://services-uat.stream.aero/STREAM.Web.Api/Production-UAT/v2/WorkPackages/import");
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
                        else
                        {
                            WriteToFile("Problem at service " + DateTime.Now);
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

    // get last execution time and execution periodicity (called ex_time) from traxdilt_set table
    public void extract_exec_times()
        {
            try
            {
                cmd = new OracleCommand();
                cmd.Connection = kcConnection;
                cmd.CommandText = "select * from traxdilt_set";
                DataTable dt = new DataTable();
                OracleDataAdapter adapter = new OracleDataAdapter(cmd);
                adapter.Fill(dt);
                last_exec_time = (DateTime)dt.Rows[0]["last_execution_time"];
                exec_time = (decimal)dt.Rows[0]["ex_time"];
            }
            catch(Exception ex)
            {
                WriteToFile("Could not extract exect_times " + ex.Message);
            }
        }

        // Write to the log database in oracle, calling oracle stored procedure for inserting values
        public void WriteLog(string xmlSource, string response, decimal status)
        {
            try
            {
                cmd = new OracleCommand();
                cmd.Connection = kcConnection;
                cmd.CommandText = "insert_traxdilt_logs_procedure";
                cmd.CommandType = CommandType.StoredProcedure;
                cmd.Parameters.Add("p_xml_source", OracleDbType.Clob).Value = xmlSource;
                cmd.Parameters.Add("p_xml_response", OracleDbType.Clob).Value = response;
                cmd.Parameters.Add("p_status", OracleDbType.Decimal).Value = status;
                cmd.ExecuteNonQuery();
            }
            catch(Exception ex)
            {
                WriteToFile("Could not write to log " + ex.Message);
            }

        }

        // after putting the workpackage to STREAM web service, update last execution time using the function below:
        public void update_exec_times(DateTime dateTime)
        {
            try
            {
                cmd = new OracleCommand();
                cmd.Connection = kcConnection;
                cmd.CommandText = "update_time_traxdilt_set_proc";
                cmd.CommandType = CommandType.StoredProcedure;
                cmd.Parameters.Add("p_last_execution_time", OracleDbType.Date).Value = dateTime;
                cmd.ExecuteNonQuery();
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
                kcConnection = new OracleConnection(ConfigurationManager.AppSettings["kcapptstConnectionString"]);
                traxConnection.Open();
                kcConnection.Open();
            }
            catch(Exception ex)
            {
                WriteToFile("Could not open connections " + ex.Message);
            }
        }

        public void closeConnections()
        {
            try
            {
                traxConnection.Close();
                kcConnection.Close();
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

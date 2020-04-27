# SolaceRTDExcel

A RealTimeData Server for streaming data from PubSub+ Event Brokers into Excel.

## Overview

Microsoft Excel provides a new worksheet function, RTD, that allows you to call a Component Object Model (COM) Automation server for the purpose of retrieving data real-time. This project builds a RTD server for streaming data from PubSub+ Event Brokers.

Currently, it can only parse Solace messages with JSON payloads, but support for other types can be easily added.

NOTE: The Solace RTD function is only supported in Windows OS x86 platforms, and cannot be deployed on a MacOS or other OS.

## Usage

```
=RTD("Solace.RTD", "", "my/topic", "SomeKey")
```

Parameters:
* The first parameter tells Excel to call our Solace RTD function. This value should always be `Solace.RTD` 
* The second parameter is always empty as we are running our function on the local machine
* The third parameter is the complete Solace Topic to subscribe and stream data from
* The fourth parameter is the key for which we want to retrieve the value. It is assumed that messages publish are in JSON format and have key-value pairs as the payload.

## Configuration

Before you add the first RTD function in Excel, you must configure the connection to a PubSub+ Broker from which you want to stream the data.

The configuration is set in the `SolaceRTDExcel.dll.config` file. The required config property to set are:
* host: the IP & Port of the PubSub+ Broker
* messageVpn: the Solace Message-VPN to connect to on the above broker
* username: the client-username to use for authentication

All other properties are optional but should be still be specified in the config file.

## Installing

1. Unzip the package downloaded from GitHub project
2. Run the `register.bat` script as an Administrator (right click on file and select Run as Administrator) to register Solace RTD with your Excel
3. Set the PubSub+ Broker configuration in `SolaceRTDExcel.dll.config` file
3. Open Excel

To unregister, run `unregister.bat` as an Administrator

## Logging

By default all logs are redirected to a file on the C:\ drive:

`C:\SolaceRTD.log`

## License

Copyright 2020 Dishant Langayan

Licensed under the Apache License, Version 2.0 (the "License");
you may not use this file except in compliance with the License.
You may obtain a copy of the License at

    http://www.apache.org/licenses/LICENSE-2.0

Unless required by applicable law or agreed to in writing, software
distributed under the License is distributed on an "AS IS" BASIS,
WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
See the License for the specific language governing permissions and
limitations under the License.
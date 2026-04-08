<%@ WebService Language="C#" Class="DataService" %>
<%
    // Web service methods would be defined here
    string GetMessage()
    {
        return "Hello from DataService";
    }

    int Add(int a, int b)
    {
        return a + b;
    }
%>

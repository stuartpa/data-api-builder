# Guidelines for AI Assistants: Configuring a DAB MCP Server per application role type.

This notebook guides AI assistants through a step-by-step chain-of-thought to generate a
dab-config-<roletype>.json file for each role type in the SQL Application

### Step 1: Create DAB configuration files for each role 

**Thought:** MCP Servers will be built using Data API Builder:

**Action:**
- Create a dab-config-<role>.json file for each MCP Server/role type in the ./Runtime folder, use this as the template:

---
```json
{
  "$schema": "https://github.com/Azure/data-api-builder/releases/download/v0.5.35/dab.draft.schema.json",
  "data-source": {
    "database-type": "mssql",
    "connection-string": "<insert the connection string here>",
    "options": {
      "set-session-context": false
    }
  },
  "runtime": {
    "rest": {
      "enabled": false,
      "path": "/api",
      "request-body-strict": true
    },
    "graphql": {
      "enabled": false,
      "path": "/graphql",
      "allow-introspection": true
    },
    "mcp": {
      "enabled": true,
    },
    "host": {
      "cors": {
        "origins": [],
        "allow-credentials": false
      },
      "authentication": {
        "provider": "Simulator"
      },
      "mode": "development",
    }
  },
  "entities": {}
}
```
---

NOTES: 
1. **Do not** create .xml files for the DAB config files, use .json files instead.
2. Make sure the parameter names don't have the @ sign in front of them!
3. **Do not** use `null` as a default value for the parameters, use `""` instead.
3. Run the script `.github/tsql/install/get-sql-connection-string.ps1` to get the connection string for the local SQL Server instance.  This will be used in the DAB config file.  
  - Specifically replace the `<insert the connection string here>` in the template with the connection string from the script.

- Then update the entities section for each config file with all operations for the role.
  - NOTE: Make sure "operations" for "all roles" are included in every config file.

For stored procedures an entity should look like this:

---
```
"<name>": {
  "source": {
    "type": "stored-procedure",
    "object": "<t-sql object name >",
    "parameters": {
      "<parameter1>": "<default value>",
      "<parameter2>": "<default value>"
      etc.
    }
  },
  "rest": {
    "methods": [ "GET" ]
  },
  "graphql": {
    "operation": "query"
  },
  "permissions": [{
   "role": "anonymous",
    "actions": [ "execute" ]
  }]
}
```
---

### Step 2: Edit the VSCode MCP Client settings

NOTE: Before proceeding with this step, you **MUST** Ask the user to open the settings.json file in VSCode and make it the current file, this is the only way you can edit the settings.json file in VSCode.

**Action:**
1. Make sure each MCP Server (one per role) is configured in the `settings.json` file in the `%APPDATA%\Code\User` folder. The file path to the settings.json file is:

---
```
%APPDATA%\Code\User\settings.json
```
---

Edit the settings.json and the mcp settings should look like this:

---
```json
"<application name> <role name>": {
    "type": "stdio",
    "command": "C:\\src\\data-api-builder\\src\\out\\cli\\net8.0\\Microsoft.DataApiBuilder.exe",
    "args": [
        "start",
        "--config",
        "<path to the role specific dab-config.json file>"
    ]
}
```
---

This is where in the settings.json file you will add the MCP Server configuration.  The settings should look like this:

---
```
{
    "mcp": {
        "servers": {
            "<application name> <role name>": {
                "type": "stdio",
                "command": "C:\\src\\data-api-builder\\src\\out\\cli\\net8.0\\Microsoft.DataApiBuilder.exe",
                "args": [
                    "start",
                    "--config",
                    "<path to the role specific dab-config.json file>"
                ]
            }
        }
    }
}
```
---

NOTES:
- If there is already a setting for an MCP server for application the role name, then just make sure it is all correct
 - Don't delete or touch any settings that are not for this application!

### THE END

CREATE DATABASE identitydb;
CREATE DATABASE campaignsdb;
CREATE DATABASE zabbixdb;

CREATE USER zabbix WITH PASSWORD 'zabbix';
GRANT ALL PRIVILEGES ON DATABASE zabbixdb TO zabbix;

\connect zabbixdb;
GRANT ALL ON SCHEMA public TO zabbix;
ALTER SCHEMA public OWNER TO zabbix;

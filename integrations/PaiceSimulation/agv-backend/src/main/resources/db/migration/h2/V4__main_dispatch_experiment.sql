CREATE TABLE IF NOT EXISTS main_dispatch_experiment (
    id BIGINT NOT NULL PRIMARY KEY,
    name VARCHAR(128) NOT NULL,
    create_user BIGINT NOT NULL,
    scheme_id VARCHAR(64) NOT NULL,
    roadnet_id BIGINT NOT NULL,
    task_set_id BIGINT NOT NULL,
    task_count INT NOT NULL,
    scenario_name VARCHAR(32) NOT NULL,
    status VARCHAR(16) NOT NULL,
    runtime_instance VARCHAR(128),
    scenario_id VARCHAR(64),
    map_definition_hash VARCHAR(64),
    report_id VARCHAR(64),
    message VARCHAR(1000),
    taskset_snapshot CLOB,
    create_time TIMESTAMP NOT NULL,
    update_time TIMESTAMP NOT NULL
);

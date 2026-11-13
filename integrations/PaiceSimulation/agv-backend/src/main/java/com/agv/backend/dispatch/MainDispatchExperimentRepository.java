package com.agv.backend.dispatch;

import org.springframework.data.jpa.repository.JpaRepository;

import java.util.List;

public interface MainDispatchExperimentRepository extends JpaRepository<MainDispatchExperiment, Long> {

    List<MainDispatchExperiment> findByCreateUserOrderByIdDesc(Long createUser);
}
